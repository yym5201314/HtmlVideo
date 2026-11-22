// Derived from the original project's virtual-clock approach, with ordered timers
// and media playback origins rather than synchronizing every clip to time zero.
(() => {
  const RealDate = Date, epoch = Date.now();
  const delay = window.setTimeout.bind(window);
  const nativeRaf = window.requestAnimationFrame.bind(window);
  let now = 0, sequence = 0;
  const timers = new Map(), rafs = new Map(), animations = new WeakMap();
  const mediaState = new WeakMap();
  const frames = new WeakMap();
  const faults = [];
  const report = e => { faults.push(String(e.stack || e)); if (faults.length > 20) faults.shift(); };
  class VirtualDate extends RealDate {
    constructor(...args) { super(...(args.length ? args : [epoch + now])); }
    static now() { return epoch + now; }
  }
  window.Date = VirtualDate;
  Object.defineProperty(performance, 'now', { configurable: true, value: () => now });
  function schedule(fn, ms, repeat, args) {
    const id = ++sequence, interval = Math.max(repeat ? 1 : 0, Number(ms) || 0);
    timers.set(id, { at: now + interval, interval: repeat ? interval : 0,
      fn: typeof fn === 'function' ? () => fn(...args) : () => (0, eval)(String(fn)) });
    return id;
  }
  window.setTimeout = (fn, ms, ...args) => schedule(fn, ms, false, args);
  window.setInterval = (fn, ms, ...args) => schedule(fn, ms, true, args);
  window.clearTimeout = window.clearInterval = id => timers.delete(id);
  window.requestAnimationFrame = fn => { const id = ++sequence; rafs.set(id, fn); return id; };
  window.cancelAnimationFrame = id => rafs.delete(id);
  window.requestIdleCallback = fn => schedule(() => fn({didTimeout: false, timeRemaining: () => 16}), 1, false, []);
  window.cancelIdleCallback = window.clearTimeout;

  const proto = HTMLMediaElement.prototype;
  const nativePause = proto.pause;
  const time = Object.getOwnPropertyDescriptor(proto, 'currentTime');
  const paused = Object.getOwnPropertyDescriptor(proto, 'paused');
  const ended = Object.getOwnPropertyDescriptor(proto, 'ended');
  const nativeDuration = Object.getOwnPropertyDescriptor(proto, 'duration');
  function state(m) {
    if (!mediaState.has(m)) mediaState.set(m, {
      playing: m.autoplay, origin: now, offset: m.autoplay ? 0 : (time.get.call(m) || 0), finished: false
    });
    return mediaState.get(m);
  }
  function position(m, s) {
    let t = s.offset + (s.playing ? (now - s.origin) / 1000 * m.playbackRate : 0);
    if (Number.isFinite(m.duration) && m.duration > 0) {
      if (m.loop) t %= m.duration;
      else if (t >= m.duration) {
        t = m.duration;
        if (s.playing) {
          s.playing = false; s.finished = true; s.offset = t;
          delay(() => m.dispatchEvent(new Event('ended')), 0);
        }
      }
    }
    return Math.max(0, t);
  }
  proto.play = function () {
    const s = state(this);
    if (!s.playing) { s.playing = true; s.origin = now; s.finished = false; }
    nativePause.call(this);
    this.dispatchEvent(new Event('play'));
    this.dispatchEvent(new Event('playing'));
    return Promise.resolve();
  };
  proto.pause = function () {
    const s = state(this);
    s.offset = position(this, s); s.playing = false; s.origin = now;
    nativePause.call(this); this.dispatchEvent(new Event('pause'));
  };
  Object.defineProperty(proto, 'paused', { configurable: true,
    get() { return mediaState.has(this) ? !state(this).playing : paused.get.call(this); } });
  Object.defineProperty(proto, 'ended', { configurable: true,
    get() { return mediaState.has(this) ? state(this).finished : ended.get.call(this); } });
  Object.defineProperty(proto, 'currentTime', { configurable: true,
    get() { return position(this, state(this)); },
    set(v) { const s = state(this); s.offset = Number(v); s.origin = now; s.finished = false; time.set.call(this, v); }
  });
  Object.defineProperty(proto, 'duration', { configurable: true,
    get() { const fr = frames.get(this); return fr ? fr.duration : nativeDuration.get.call(this); } });
  function showFrame(m, fr) {
    const idx = Math.max(0, Math.min(fr.count - 1, Math.round(position(m, state(m)) * fr.fps)));
    const src = 'url("' + fr.base + String(idx).padStart(5, '0') + '.jpg")';
    if (m.style.backgroundImage !== src) {
      m.style.backgroundImage = src;
      m.style.backgroundSize = 'cover';
      m.style.backgroundPosition = 'center';
    }
  }
  async function syncMedia() {
    await Promise.all(Array.from(document.querySelectorAll('audio,video')).map(async m => {
      const fr = frames.get(m);
      if (fr) { showFrame(m, fr); return; }
      if (m.readyState < 1) return;
      const s = state(m), target = position(m, s);
      nativePause.call(m);
      if (Math.abs(time.get.call(m) - target) < 0.001) return;
      const complete = new Promise(resolve => {
        const finish = () => { m.removeEventListener('seeked', finish); resolve(); };
        m.addEventListener('seeked', finish, { once: true });
        delay(finish, 3000);
      });
      time.set.call(m, target);
      await complete;
    }));
  }
  function syncAnimations() {
    for (const a of document.getAnimations()) {
      if (!animations.has(a)) {
        animations.set(a, { origin: now,
          offset: typeof a.animationName === 'string' ? 0 : (Number(a.currentTime) || 0), rate: a.playbackRate });
        a.pause();
      }
      const s = animations.get(a);
      a.currentTime = s.offset + (now - s.origin) * s.rate;
    }
  }
  async function step(ms) {
    syncAnimations();
    const target = now + ms;
    let count = 0;
    for (;;) {
      let entry = null, id = null;
      for (const [key, value] of timers) {
        if (value.at <= target && (!entry || value.at < entry.at)) { entry = value; id = key; }
      }
      if (!entry) break;
      if (++count > 10000) throw Error('Too many virtual timers in one frame');
      now = Math.max(now, entry.at);
      if (entry.interval) entry.at += entry.interval; else timers.delete(id);
      try { entry.fn(); } catch (e) { report(e); }
      await Promise.resolve();
    }
    now = target;
    await syncMedia();
    const queue = Array.from(rafs);
    for (const [id] of queue) rafs.delete(id);
    for (const [, fn] of queue) {
      try { fn(now); } catch (e) { report(e); }
      await Promise.resolve();
    }
    void document.documentElement.offsetHeight;
    syncAnimations();
    await syncMedia();
    void document.documentElement.offsetHeight;
    // Wait a real compositor frame so transform/opacity changes are committed
    // before the CDP screenshot, otherwise some frames flicker with stale state.
    await new Promise(resolve => {
      let settled = false;
      const done = () => { if (!settled) { settled = true; resolve(); } };
      nativeRaf(() => nativeRaf(done));
      delay(done, 40);
    });
    return now;
  }
  window.addEventListener('error', e => report(e.error || e.message));
  window.addEventListener('unhandledrejection', e => report(e.reason || 'Unhandled rejection'));
  function useFrames(map) {
    for (const m of document.querySelectorAll('video')) {
      const src = m.currentSrc || m.src || (m.querySelector('source[src]') || {}).src;
      const info = map[src];
      if (!info) continue;
      const fr = { base: info.base, fps: info.fps, count: info.count,
        duration: Number.isFinite(m.duration) ? m.duration : 0 };
      frames.set(m, fr);
      m.removeAttribute('src');
      m.removeAttribute('poster');
      m.load();
      showFrame(m, fr);
    }
  }
  window.__hvl = { step, delay, useFrames, faults };
})();
