window.__hvl.prepare = async function (options) {
  const clock = window.__hvl;
  // Initialize at t=0; preparation must not consume auto-playing opening frames.
  await clock.step(0);
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
  let playback = '自动播放 / 静态页面';
  let button = options.start ? document.querySelector(options.start) : null;
  if (options.start && !button) throw Error('Start selector did not match: ' + options.start);
  if (!options.start) {
    const visible = el => { const r = el.getBoundingClientRect(), s = getComputedStyle(el);
      return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden' && !el.disabled; };
    button = Array.from(document.querySelectorAll(
      'button,[role="button"],#startBtn,#btnStart,#bigplay,[data-play],.play-btn,.start-btn'
    )).find(el => visible(el) && /播放|开始|重播|play|start|replay/i.test(
      (el.id || '')+' '+el.className+' '+(el.textContent || '')+' '+(el.getAttribute('aria-label') || '')));
  }
  if (button) {
    button.click(); // Exactly once; do not synthesize a second click.
    playback = '点击：' + (options.start || button.id || button.textContent.trim().slice(0,30));
    await Promise.resolve();
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
  return {stage: selector || '完整视口', playback, missing};
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
