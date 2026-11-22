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
  let drivenFrame = null, drivenAnimations = [];
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
    const frame = document.querySelector('.frame.on');
    let moving = false;
    // Pages without a .frame.on marker drive every animation each tick.
    if (!frame) {
      drivenFrame = null;
      drivenAnimations = [];
      for (const a of document.getAnimations()) {
        if (!animations.has(a)) {
          animations.set(a, { origin: now,
            offset: typeof a.animationName === 'string' ? 0 : (Number(a.currentTime) || 0), rate: a.playbackRate });
          a.pause();
        }
        const s = animations.get(a);
        const t = s.offset + (now - s.origin) * s.rate;
        if (t !== s.last) { s.last = t; a.currentTime = t; if (s.rate) moving = true; }
      }
      return moving;
    }
    // Frame-based pages: rebuild the driven list only when the active frame
    // changes, so per-tick work stays at one frame's effects instead of
    // hundreds of hidden particles stalling Page.captureScreenshot.
    if (frame !== drivenFrame) {
      drivenFrame = frame;
      drivenAnimations = [];
      for (const a of document.getAnimations()) {
        const target = a.effect && a.effect.target;
        if (!animations.has(a)) {
          animations.set(a, { origin: now,
            offset: typeof a.animationName === 'string' ? 0 : (Number(a.currentTime) || 0), rate: a.playbackRate });
          a.pause();
        }
        if (target && frame.contains(target)) drivenAnimations.push(a);
      }
    }
    for (const a of drivenAnimations) {
      const s = animations.get(a);
      const t = s.offset + (now - s.origin) * s.rate;
      if (t !== s.last) { s.last = t; a.currentTime = t; if (s.rate) moving = true; }
    }
    return moving;
  }
  // settle=false skips the compositor round trip, media=false skips native seeks.
  // Fast-forward uses both: only page state has to advance, nothing is captured.
  async function step(ms, settle, media) {
    if (settle === undefined) settle = true;
    if (media === undefined) media = true;
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
    if (media) await syncMedia();
    const queue = Array.from(rafs);
    for (const [id] of queue) rafs.delete(id);
    for (const [, fn] of queue) {
      try { fn(now); } catch (e) { report(e); }
      await Promise.resolve();
    }
    if (media) {
      void document.documentElement.offsetHeight;
      await syncMedia();
    }
    const moving = syncAnimations();
    if (media) void document.documentElement.offsetHeight;
    if (settle) {
      // One compositor frame is enough: Page.captureScreenshot already forces the
      // next commit, and a second rAF doubles the wait on heavy pages.
      await new Promise(resolve => {
        let settled = false;
        const done = () => { if (!settled) { settled = true; resolve(); } };
        nativeRaf(done);
        delay(done, 40);
      });
    }
    // Nothing timer-driven ran, no animation advanced and the page has no
    // self-updating media, so this picture equals the previous one and the
    // caller may reuse the last capture instead of paying for a new screenshot.
    return { now, idle: !moving && count === 0 && queue.length === 0 && !window.__hvl.animated };
  }
  // Replay the virtual timeline up to an absolute timestamp without capturing.
  // Deterministic pages produce the same state as stepping frame by frame, which
  // is what lets several browser processes render different ranges in parallel.
  async function jump(target) {
    const dt = 1000 / (Number(window.__hvl.fps) || 30);
    let guard = 0;
    while (now < target - 1e-6) {
      if (++guard > 200000) throw Error('Virtual clock fast-forward is too long');
      await step(Math.min(dt, target - now), false, false);
    }
    await step(0, true, true);
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
  window.__hvl = { step, jump, delay, useFrames, faults, fps: 30, animated: false };
})();
