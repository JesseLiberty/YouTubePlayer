// Hosts the YouTube IFrame player and reports playback progress to .NET, matching
// the messages the WPF PlayerWindow received from its WebView2 script.
let apiReady;

function loadApi() {
  if (window.YT && window.YT.Player) return Promise.resolve();
  apiReady ??= new Promise(resolve => {
    const previous = window.onYouTubeIframeAPIReady;
    window.onYouTubeIframeAPIReady = () => {
      previous?.();
      resolve();
    };
    const script = document.createElement('script');
    script.src = 'https://www.youtube.com/iframe_api';
    document.head.appendChild(script);
  });
  return apiReady;
}

export async function open(host, videoId, startSeconds, dotNet) {
  await loadApi();

  const target = document.createElement('div');
  host.replaceChildren(target);

  let disposed = false;
  let started = false;
  let watched = false;
  let lastSecond = -1;
  let player;

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
      dotNet.invokeMethodAsync('OnPosition', second);
    }
    if (!started && time >= 1) {
      started = true;
      dotNet.invokeMethodAsync('OnStarted');
    }
    if (!watched && time / duration >= 0.9) {
      watched = true;
      dotNet.invokeMethodAsync('OnWatched');
    }
  };

  player = new YT.Player(target, {
    videoId,
    width: '100%',
    height: '100%',
    playerVars: { autoplay: 1, start: Math.max(0, Math.floor(startSeconds)), rel: 0, playsinline: 1 },
    events: {
      onReady: e => e.target.playVideo(),
      onStateChange: e => {
        if (disposed || e.data !== YT.PlayerState.ENDED) return;
        const duration = player.getDuration();
        dotNet.invokeMethodAsync('OnEnded', Number.isFinite(duration) ? Math.floor(duration) : -1);
      },
    },
  });

  const timer = setInterval(report, 250);

  return {
    dispose() {
      disposed = true;
      clearInterval(timer);
      try { player.destroy(); } catch { }
      host.replaceChildren();
    },
  };
}
