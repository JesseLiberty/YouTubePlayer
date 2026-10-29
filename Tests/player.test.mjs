import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import test from 'node:test';

const source = await readFile(resolve(import.meta.dirname, '..', 'YouTubeWeb', 'wwwroot', 'js', 'player.js'), 'utf8');
const flush = () => new Promise(resolve => setImmediate(resolve));
let importId = 0;

async function setup(ready = false) {
  const scripts = [];
  const timeouts = new Map();
  const intervals = new Map();
  const players = [];
  const calls = [];
  let nextTimer = 0;
  globalThis.window = {};
  globalThis.document = {
    createElement: tag => ({ tag, remove() { scripts.splice(scripts.indexOf(this), 1); } }),
    head: { appendChild: script => scripts.push(script) },
  };
  globalThis.setTimeout = callback => { const id = ++nextTimer; timeouts.set(id, callback); return id; };
  globalThis.clearTimeout = id => timeouts.delete(id);
  globalThis.setInterval = callback => { const id = ++nextTimer; intervals.set(id, callback); return id; };
  globalThis.clearInterval = id => intervals.delete(id);
  const installApi = () => {
    globalThis.YT = window.YT = {
      PlayerState: { PLAYING: 1, PAUSED: 2, ENDED: 0 },
      Player: class {
        constructor(target, options) {
          this.options = options;
          this.destroyed = 0;
          this.time = 91;
          this.duration = 100;
          this.videoId = options.videoId;
          players.push(this);
        }
        getPlayerState() { return YT.PlayerState.PLAYING; }
        getVideoData() { return { video_id: this.videoId }; }
        getCurrentTime() { return this.time; }
        getDuration() { return this.duration; }
        playVideo() { this.played = true; }
        destroy() { this.destroyed++; }
      },
    };
  };
  if (ready) installApi();
  const host = { isConnected: true, replaceChildren(...children) { this.children = children; } };
  const dotNet = {
    invokeMethodAsync(method, ...args) {
      calls.push([method, ...args]);
      return Promise.resolve();
    },
  };
  const module = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}#${importId++}`);
  return { module, host, dotNet, calls, players, scripts, timeouts, intervals, installApi };
}

test('script errors are reported, removed, and retry starts a fresh load', async () => {
  const f = await setup();
  f.module.open(f.host, 'video', 0, f.dotNet, 'failed');
  assert.equal(f.scripts.length, 1);
  f.scripts[0].onerror();
  await flush();
  assert.equal(f.scripts.length, 0);
  assert.equal(f.timeouts.size, 0);
  assert.match(f.calls[0][2], /connection.*retry/);
  assert.deepEqual(f.calls[0].slice(0, 2), ['OnError', 'failed']);
  const handle = f.module.open(f.host, 'video', 5, f.dotNet, 'retry');
  assert.equal(f.scripts.length, 1);
  f.installApi();
  window.onYouTubeIframeAPIReady();
  await flush();
  assert.equal(f.players.length, 1);
  assert.equal(f.players[0].options.playerVars.start, 5);
  handle.dispose();
});

test('script load timeout is recoverable and restores a previous ready callback', async () => {
  const f = await setup();
  const previous = () => {};
  window.onYouTubeIframeAPIReady = previous;
  f.module.open(f.host, 'video', 0, f.dotNet, 'timeout');
  [...f.timeouts.values()][0]();
  await flush();
  assert.equal(window.onYouTubeIframeAPIReady, previous);
  assert.equal(f.calls[0][0], 'OnError');
  f.module.open(f.host, 'video', 0, f.dotNet, 'retry');
  assert.equal(f.scripts.length, 1);
  f.installApi();
  window.onYouTubeIframeAPIReady();
  await flush();
  assert.equal(f.players.length, 1);
  f.module.close(f.host, 'retry');
});

test('closing a pending open prevents late iframe and callback creation', async () => {
  const f = await setup();
  const handle = f.module.open(f.host, 'video', 0, f.dotNet, 'closed');
  f.module.close(f.host, 'closed');
  f.installApi();
  window.onYouTubeIframeAPIReady();
  await flush();
  assert.equal(f.players.length, 0);
  assert.equal(f.intervals.size, 0);
  assert.equal(f.calls.length, 0);
  handle.dispose();
});

test('a removed host cannot create a player after API loading', async () => {
  const f = await setup();
  f.module.open(f.host, 'video', 0, f.dotNet, 'removed');
  f.host.isConnected = false;
  f.installApi();
  window.onYouTubeIframeAPIReady();
  await flush();
  assert.equal(f.players.length, 0);
});

test('YouTube errors are actionable, stop polling, and ignore late events', async () => {
  const f = await setup(true);
  const handle = f.module.open(f.host, 'video', 0, f.dotNet, 'error');
  await flush();
  const player = f.players[0];
  player.options.events.onError({ data: 150 });
  assert.match(f.calls[0][2], /embedded.*Retry.*YouTube/);
  assert.equal(player.destroyed, 1);
  assert.equal(f.intervals.size, 0);
  player.options.events.onReady({ target: player });
  player.options.events.onStateChange({ data: YT.PlayerState.ENDED });
  assert.equal(player.played, undefined);
  assert.equal(f.calls.length, 1);
  handle.dispose();
  assert.equal(player.destroyed, 1);
});

test('progress and end callbacks carry session identity and preserve watch thresholds', async () => {
  const f = await setup(true);
  f.module.open(f.host, 'video', 0, f.dotNet, 'progress');
  await flush();
  const report = [...f.intervals.values()][0];
  const player = f.players[0];
  player.videoId = 'advertisement';
  report();
  assert.equal(f.calls.length, 0);
  player.videoId = 'video';
  report();
  report();
  assert.deepEqual(f.calls, [
    ['OnPosition', 'progress', 91], ['OnStarted', 'progress'], ['OnWatched', 'progress'],
  ]);
  player.options.events.onStateChange({ data: YT.PlayerState.ENDED });
  assert.deepEqual(f.calls.at(-1), ['OnEnded', 'progress', 100]);
  f.module.close(f.host, 'progress');
  assert.equal(f.intervals.size, 0);
});

test('old handles and stale closes cannot destroy a replacement session', async () => {
  const f = await setup(true);
  const old = f.module.open(f.host, 'old-video', 0, f.dotNet, 'old');
  await flush();
  const current = f.module.open(f.host, 'new-video', 0, f.dotNet, 'new');
  await flush();
  const children = f.host.children;
  old.dispose();
  f.module.close(f.host, 'old');
  assert.equal(f.host.children, children);
  assert.equal(f.players[0].destroyed, 1);
  assert.equal(f.players[1].destroyed, 0);
  assert.equal(f.intervals.size, 1);
  current.dispose();
  assert.equal(f.intervals.size, 0);
});

test('late .NET callback rejections are observed after disconnect', async () => {
  const f = await setup(true);
  f.dotNet.invokeMethodAsync = () => Promise.reject(new Error('Disconnected'));
  f.module.open(f.host, 'video', 0, f.dotNet, 'disconnect');
  await flush();
  [...f.intervals.values()][0]();
  f.players[0].options.events.onError({ data: 100 });
  await flush();
  assert.equal(f.intervals.size, 0);
});
