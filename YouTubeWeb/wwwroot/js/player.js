// Hosts the YouTube IFrame player and reports playback progress to .NET, matching
// the messages the WPF PlayerWindow received from its WebView2 script.
let apiReady;
const sessions = new WeakMap();

function loadApi() {
  if (window.YT && window.YT.Player) return Promise.resolve();
  apiReady ??= new Promise((resolve, reject) => {
    const previous = window.onYouTubeIframeAPIReady;
    const cleanup = () => {
      clearTimeout(timeout);
      script.onerror = null;
      if (window.onYouTubeIframeAPIReady === ready)
        window.onYouTubeIframeAPIReady = previous;
    };
    const fail = () => {
      cleanup();
      script.remove();
      reject(new Error('YouTube could not load. Check your connection or content blocker, then retry.'));
    };
    const ready = () => {
      cleanup();
      try { previous?.(); }
      finally {
        if (window.YT?.Player) resolve();
        else reject(new Error('YouTube did not initialize. Please retry.'));
      }
    };
    const script = document.createElement('script');
    script.src = 'https://www.youtube.com/iframe_api';
    script.onerror = fail;
    const timeout = setTimeout(fail, 15000);
    window.onYouTubeIframeAPIReady = ready;
    document.head.appendChild(script);
  }).catch(error => {
    apiReady = undefined;
    throw error;
  });
  return apiReady;
}

export function open(host, videoId, startSeconds, dotNet, sessionId) {
  sessions.get(host)?.dispose();
  let disposed = false;
  let started = false;
  let watched = false;
  let lastSecond = -1;
  let player;
  let timer;
  const notify = (method, ...args) => {
    if (!disposed)
      dotNet.invokeMethodAsync(method, sessionId, ...args).catch(() => {});
  };
  const fail = message => {
    if (disposed) return;
    notify('OnError', message);
    handle.dispose();
  };

  const isMainVideo = () => {
    const state = player?.getPlayerState?.();
    return (state === YT.PlayerState.PLAYING || state === YT.PlayerState.PAUSED) &&
      player.getVideoData?.().video_id === videoId;
  };

  const report = () => {
    if (disposed || !isMainVideo()) return;
    const time = player.getCurrentTime();
    const duration = player.getDuration();
    if (!duration) return;

    const second = Math.floor(time);
    if (second !== lastSecond) {
      lastSecond = second;
      notify('OnPosition', second);
    }
    if (!started && time >= 1) {
      started = true;
      notify('OnStarted');
    }
    if (!watched && time / duration >= 0.9) {
      watched = true;
      notify('OnWatched');
    }
  };

  const handle = {
    sessionId,
    dispose() {
      if (disposed) return;
      disposed = true;
      clearInterval(timer);
      try { player?.destroy(); } catch { }
      if (sessions.get(host) === handle) {
        sessions.delete(host);
        host.replaceChildren();
      }
    },
  };
  sessions.set(host, handle);

  // Return the cancellation handle before waiting for the external script.
  loadApi().then(() => {
    if (disposed || !host.isConnected) {
      handle.dispose();
      return;
    }
    const target = document.createElement('div');
    host.replaceChildren(target);
    player = new YT.Player(target, {
      videoId,
      width: '100%',
      height: '100%',
      playerVars: { autoplay: 1, start: Math.max(0, Math.floor(startSeconds)), rel: 0, playsinline: 1 },
      events: {
        onReady: e => { if (!disposed) e.target.playVideo(); },
        onError: e => {
          const message = {
            2: 'The video address is invalid.',
            5: 'Your browser could not play this video.',
            100: 'This video is unavailable or private.',
            101: 'This video cannot be played in an embedded player.',
            150: 'This video cannot be played in an embedded player.',
            153: 'YouTube could not verify this player.',
          }[e.data] ?? 'YouTube could not play this video.';
          fail(`${message} Retry, or open the video on YouTube.`);
        },
        onStateChange: e => {
          if (disposed || e.data !== YT.PlayerState.ENDED) return;
          const duration = player.getDuration();
          notify('OnEnded', Number.isFinite(duration) ? Math.floor(duration) : -1);
        },
      },
    });
    if (disposed) {
      try { player.destroy(); } catch { }
      return;
    }
    timer = setInterval(report, 250);
  }).catch(error => fail(error.message));

  return handle;
}

export function close(host, sessionId) {
  const handle = sessions.get(host);
  if (handle?.sessionId === sessionId) handle.dispose();
}
