window.__hvl.prepare = async function (options) {
  const clock = window.__hvl;
  // Fast-forward granularity: one rendered frame, so a jumped page is at the
  // same state a frame-by-frame render would have reached.
  clock.fps = Number(options.fps) > 0 ? Number(options.fps) : 30;
  // Initialize at t=0; preparation must not consume auto-playing opening frames.
  await clock.step(0);
  // Self-updating content (canvas/video/GIF/SMIL) can repaint without any timer
  // or animation, so it must disable the idle-screenshot shortcut.
  clock.animated = !!document.querySelector('canvas,video,img[src*=".gif"],svg animate');
  const bounded = promise => Promise.race([promise, new Promise(r => clock.delay(r, 8000))]);
  if (document.fonts) await bounded(document.fonts.ready);
  await bounded(Promise.all(Array.from(document.images).filter(im => !im.complete).map(im =>
    new Promise(r => { im.addEventListener('load', r, {once:true}); im.addEventListener('error', r, {once:true}); }))));
  await bounded(Promise.all(Array.from(document.querySelectorAll('audio,video')).filter(m => m.readyState < 2).map(m =>
    new Promise(r => { m.addEventListener('loadeddata', r, {once:true}); m.addEventListener('error', r, {once:true}); }))));

  let stage = null, selector = options.stage;
  if (selector) {
    stage = document.querySelector(selector);
    if (!stage) throw Error('Stage selector did not match: ' + selector);
  } else if (options.fit) {
    for (const sel of ['#stage','.stage','#player','.player','#screen','#app','#root','main','canvas','video']) {
      const el = document.querySelector(sel);
      if (el) { const r = el.getBoundingClientRect();
        if (r.width >= 120 && r.height >= 80) { stage = el; selector = sel; break; } }
    }
  }
  if (stage && options.fit) {
    const r = stage.getBoundingClientRect(), scale = Math.min(innerWidth / r.width, innerHeight / r.height);
    // Preserve the stage's coordinate system; resizing alone crops fixed-size scenes.
    for (let p = stage.parentElement; p; p = p.parentElement) {
      p.style.setProperty('transform', 'none', 'important');
      p.style.setProperty('filter', 'none', 'important');
      p.style.setProperty('perspective', 'none', 'important');
      p.style.setProperty('overflow', 'visible', 'important');
    }
    Object.assign(stage.style, {position:'fixed', left:(innerWidth-r.width*scale)/2+'px',
      top:(innerHeight-r.height*scale)/2+'px', width:r.width+'px', height:r.height+'px',
      maxWidth:'none', maxHeight:'none', margin:'0', transformOrigin:'0 0',
      transform:'scale('+scale+')'});
  }
  document.documentElement.style.overflow = 'hidden';
  document.body.style.overflow = 'hidden';
  // Drop compositor layers for hidden frames so heavy pages keep GPU memory
  // for the visible frame instead of dozens of off-screen layers.
  if (!document.getElementById('__hvl_layers')) {
    const layerStyle = document.createElement('style');
    layerStyle.id = '__hvl_layers';
    layerStyle.textContent = '.frame:not(.on),.frame:not(.on) *{will-change:auto !important;filter:none !important;' +
      'backdrop-filter:none !important;-webkit-backdrop-filter:none !important}' +
      '.frame:not(.on) .fx,.frame:not(.on) .fx *{display:none !important}';
    (document.head || document.documentElement).appendChild(layerStyle);
  }
  let playback = '自动播放 / 静态页面', clicked = false;
  const visible = el => { const r = el.getBoundingClientRect(), s = getComputedStyle(el);
    return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden' && !el.disabled; };
  const area = el => { const r = el.getBoundingClientRect(); return r.width * r.height; };
  const meta = el => (el.id || '') + ' ' + (typeof el.className === 'string' ? el.className : '') + ' ' +
    (el.getAttribute('aria-label') || '') + ' ' + (el.getAttribute('title') || '');
  const press = /播放|开始|开场|放映|play|start/i, again = /重播|重新|replay|restart/i;
  // Pass 1: a real control. Wording on the control itself is the strongest signal:
  // a page can carry "play" in an id while the label means pause (#playBtn = 暂停
  // next to the real #startBtn 开始放映), and pressing that one leaves the cover up.
  // Among equally labelled controls the smallest box wins, so a play button nested
  // in a full-screen cover is pressed instead of the cover wrapper.
  function control() {
    const scored = [];
    for (const el of document.querySelectorAll(
      'button,[role="button"],a,input[type="button"],input[type="submit"],' +
      '#startBtn,#btnStart,#bigplay,[data-play],[data-start],[data-begin],' +
      '.play-btn,.start-btn,.btn,.button,[class*="play"],[class*="start"]')) {
      if (!visible(el)) continue;
      const text = (el.textContent || '').trim(), attr = meta(el);
      let rank;
      if (press.test(text)) rank = 0;
      else if (again.test(text)) rank = 1;
      else if (press.test(attr) || again.test(attr)) rank = 2;
      else continue;
      scored.push({ el: el, rank: rank, area: area(el) });
    }
    scored.sort((a, b) => a.rank - b.rank || a.area - b.area);
    return scored.length ? scored[0].el : null;
  }
  // Pass 2 (no control found): plenty of pages only make "click the cover" start
  // playback. Press the innermost wrapper at the middle of the picture that looks
  // like a cover / intro layer, or a large pointer-cursor surface. Anything else -
  // a plain article body, a link, a caption - is left alone, so a genuinely static
  // page is not clicked at all.
  function overlay() {
    const hints = /cover|intro|splash|enter|start|play|poster|hero|opening|landing/i;
    let el = document.elementFromPoint(innerWidth / 2, innerHeight / 2);
    for (let up = 0; el && el !== document.body && up < 12; up++, el = el.parentElement) {
      if (!visible(el)) continue;
      // A page root (#stage/#app directly under <body>) is not a cover: pressing it
      // is just a random click on the picture, so leave static pages alone.
      if (!el.parentElement || el.parentElement === document.body ||
        el.parentElement === document.documentElement) continue;
      if (hints.test(meta(el))) return el;
      if (area(el) >= innerWidth * innerHeight * 0.4 && getComputedStyle(el).cursor === 'pointer') return el;
    }
    return null;
  }
  let button = options.start ? document.querySelector(options.start) : null;
  if (options.start && !button) throw Error('Start selector did not match: ' + options.start);
  let guessed = false;
  if (!options.start) {
    button = control();
    if (!button) { button = overlay(); guessed = button != null; }
  }
  if (button) {
    button.click(); // Exactly once; do not synthesize a second click.
    clicked = true;
    const text = (button.textContent || '').trim().slice(0,30);
    const cls = typeof button.className === 'string' ? button.className.trim() : '';
    // Name a guessed cover by its id/class; name a real control by its label.
    const tag = options.start || (guessed ? (button.id || cls) : (button.id || text)) || '画面中心';
    playback = '点击：' + tag.trim().slice(0,30) + (guessed ? '（未识别到播放按钮）' : '');
    await Promise.resolve();
  } else if (!options.start) {
    playback = '未点击（未识别到播放入口）';
  }
  if (options.hide && stage && stage !== document.body) {
    let child = stage;
    while (child.parentElement && child.parentElement !== document.documentElement) {
      for (const sibling of child.parentElement.children)
        if (sibling !== child && !/^(SCRIPT|STYLE|LINK|AUDIO)$/.test(sibling.tagName))
          sibling.style.visibility = 'hidden';
      child = child.parentElement;
    }
  }
  const missing = Array.from(document.images).filter(im => !im.naturalWidth).length;
  await clock.step(0);
  return {stage: selector || '完整视口', playback, clicked, missing};
};

window.__hvl.probe = function () {
  const valid = n => typeof n === 'number' && Number.isFinite(n) && n >= 0.2 && n <= 3600;
  for (const name of ['TOTAL','DURATION','TOTAL_TIME','TOTAL_DURATION','totalDuration','totalTime','videoDuration']) {
    try { const n = (0,eval)(name); if (valid(n)) return {duration:n, source:name}; } catch (_) {}
  }
  for (const name of ['TL','SHOTS','SCENES','SEGMENTS','CLIPS','TIMELINE','scenes','timeline']) {
    try {
      const array = (0,eval)(name);
      if (!Array.isArray(array) || !array.length) continue;
      for (const key of ['d','dur','duration']) {
        if (array.every(x => x && valid(x[key]))) {
          const n = array.reduce((a,x) => a+x[key],0);
          if (valid(n)) return {duration:n, source:name+'.'+key};
        }
      }
    } catch (_) {}
  }
  const data = Number(document.body.dataset.duration || document.documentElement.dataset.duration);
  if (valid(data)) return {duration:data, source:'data-duration'};
  const total = document.querySelector('#tcAll,#tc-all,.total-time,.total-duration');
  if (total) {
    const m = /(?:(\d+):)?(\d+):(\d{1,2}(?:\.\d+)?)/.exec(total.textContent);
    if (m) { const n = Number(m[1]||0)*3600+Number(m[2])*60+Number(m[3]);
      if (valid(n)) return {duration:n,source:'时间码'}; }
  }
  let maximum = 0;
  for (const m of document.querySelectorAll('audio,video'))
    if (valid(m.duration)) maximum = Math.max(maximum,m.duration);
  if (maximum) return {duration:maximum,source:'媒体时长'};
  for (const a of document.getAnimations()) {
    const t = a.effect.getComputedTiming();
    if (valid(t.endTime / 1000)) maximum = Math.max(maximum,t.endTime/1000);
  }
  return {duration:maximum,source:maximum ? 'CSS 动画' : ''};
};

window.__hvl.audio = function () {
  // Selector lists use document order, not selector priority: audio must come first.
  const media = [...document.querySelectorAll('audio'),
    ...document.querySelectorAll('video')];
  return Array.from(new Set(media.map(m =>
    m.currentSrc || m.src || (m.querySelector('source[src]') || {}).src
  ).filter(Boolean)));
};

window.__hvl.missing = function () {
  const resources = [];
  for (const im of document.images)
    if (!im.naturalWidth) resources.push(im.currentSrc || im.src);
  for (const m of document.querySelectorAll('audio,video'))
    if (m.error || m.readyState < 2) resources.push(m.currentSrc || m.src);
  return Array.from(new Set(resources.filter(Boolean))).slice(0, 20);
};

window.__hvl.videos = function () {
  const seen = new Set();
  const out = [];
  for (const m of document.querySelectorAll('video')) {
    const src = m.currentSrc || m.src || (m.querySelector('source[src]') || {}).src;
    if (!src || seen.has(src)) continue;
    seen.add(src);
    out.push(src);
  }
  return out;
};
