const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'YouTubePlayer', 'PlayerWindow.xaml.cs'), 'utf8');
const template = source.match(/private const string EndedScript = """\r?\n([\s\S]*?)\r?\n\s*""";/)[1];

function setup(durationSeconds = 600) {
  const handlers = {};
  const messages = [];
  const pending = [];
  const classes = new Set();
  const player = {
    id: 'movie_player',
    state: 1,
    videoId: 'requested',
    classList: { contains: value => classes.has(value) },
    getVideoData() { return { video_id: this.videoId }; },
    getPlayerState() { return this.state; }
  };
  class Video {
    duration = 600;
    currentTime = 45;
    ended = false;
    main = true;
    closest() { return player; }
    matches() { return this.main; }
  }
  const location = { pathname: '/watch', search: '?v=requested' };
  const window = { chrome: { webview: { postMessage: value => messages.push(value) } } };
  window.top = window;
  vm.runInNewContext(template.replace('__PLAYBACK__', JSON.stringify({ videoId: 'requested', durationSeconds })), {
    window, location, HTMLVideoElement: Video, URLSearchParams,
    document: { addEventListener: (event, handler) => { handlers[event] = handler; } },
    setTimeout: callback => pending.push(callback)
  });
  const video = new Video();
  return {
    video, player, classes, messages, location,
    emit: event => handlers[event]({ target: video }),
    flush: () => { while (pending.length) pending.shift()(); }
  };
}

for (const seconds of [30, 60]) {
  const test = setup();
  Object.assign(test.video, { duration: seconds, currentTime: seconds, ended: true });
  test.player.state = 0;
  test.emit('timeupdate');
  test.emit('ended');
  test.flush();
  assert.deepEqual(test.messages, [], 'An ad with cleared ad CSS must not save progress or close the player.');
}

for (const change of [
  test => test.classes.add('ad-showing'),
  test => test.classes.add('ad-interrupting'),
  test => { test.video.main = false; },
  test => { test.player.id = 'preview_player'; },
  test => { test.location.search = '?v=other'; },
  test => { test.player.videoId = 'other'; },
  test => { test.player.state = 1; },
  test => { test.video.currentTime = 45; },
  test => { test.video.ended = false; }
]) {
  const test = setup();
  Object.assign(test.video, { currentTime: 600, ended: true });
  test.player.state = 0;
  change(test);
  test.emit('ended');
  test.flush();
  assert.deepEqual(test.messages, [], 'Non-content or premature ended event closed the player.');
}

const resumed = setup();
resumed.video.currentTime = 300;
resumed.emit('timeupdate');
assert.deepEqual(resumed.messages, ['position:requested:300', 'started:requested']);
resumed.messages.length = 0;
resumed.video.currentTime = 540;
resumed.emit('timeupdate');
assert.deepEqual(resumed.messages, ['position:requested:540', 'watched:requested']);

for (const duration of [600, null]) {
  const test = setup(duration);
  Object.assign(test.video, { currentTime: 600, ended: true });
  test.emit('ended');
  assert.deepEqual(test.messages, [], 'End check must wait for the player state transition.');
  test.player.state = 0;
  test.flush();
  assert.deepEqual(test.messages, ['position:requested:600', 'ended:requested']);
}

const shortVideo = setup(30);
Object.assign(shortVideo.video, { duration: 30, currentTime: 30, ended: true });
shortVideo.player.state = 0;
shortVideo.emit('ended');
shortVideo.flush();
assert.deepEqual(shortVideo.messages, ['position:requested:30', 'ended:requested'],
  'A genuinely short requested video should still close at its end.');

const adTransition = setup();
Object.assign(adTransition.video, { duration: 60, currentTime: 60, ended: true });
adTransition.classes.add('ad-showing');
adTransition.emit('ended');
adTransition.classes.clear();
adTransition.player.state = 0;
adTransition.flush();
assert.deepEqual(adTransition.messages, [], 'Clearing the ad class before the end check must not close the player.');

const transition = setup();
Object.assign(transition.video, { currentTime: 600, ended: true });
transition.emit('ended');
Object.assign(transition.video, { currentTime: 0, ended: false });
transition.flush();
assert.deepEqual(transition.messages, [], 'A replaced media source must not close the player.');

console.log('PASS: ad endings, unrelated players, premature events, resume progress, watch marks, real endings, and source transitions.');
