(() => {
  const C = window.IANALITICA || {}, $ = (s, r = document) => r.querySelector(s), $$ = (s, r = document) => [...r.querySelectorAll(s)];
  // menú móvil
  const nav = $('.nav'), bur = $('.burger');
  bur?.addEventListener('click', () => { const o = nav.classList.toggle('open'); bur.setAttribute('aria-expanded', o); });
  // «Ingresar» y correo del pie: ocultos en el HTML; aparecen solo con valores reales en config.js
  const valido = x => !!x && !x.includes('por-definir'), correoOk = valido(C.correoComercial);
  if (valido(C.urlPortal) && !C.urlPortal.startsWith('#')) $$('a[data-portal]').forEach(a => { a.href = C.urlPortal; (a.closest('li') || a).hidden = false; });
  if (correoOk) $$('[data-correo]').forEach(li => { const a = document.createElement('a'); a.href = 'mailto:' + C.correoComercial; a.textContent = C.correoComercial; li.append(a); li.hidden = false; });
  // aparición al hacer scroll
  const io = 'IntersectionObserver' in window ? new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) { e.target.classList.add('v'); io.unobserve(e.target); } }), { threshold: .12 }) : null;
  $$('.rev').forEach(e => io ? io.observe(e) : e.classList.add('v'));
  // medios: la imagen fija va siempre; el video se monta solo en > 640 px, sin «reducir movimiento» ni ahorro de datos, al acercarse a la vista
  const conVideo = matchMedia('(min-width: 641px)').matches && !matchMedia('(prefers-reduced-motion: reduce)').matches && !navigator.connection?.saveData;
  const activar = fig => {
    const v = $('video[data-video]', fig), img = $('img', fig), nota = $('[data-nota-video]', fig);
    v.poster = img.currentSrc || img.src;
    v.innerHTML = `<source src="${v.dataset.webm}" type="video/webm"><source src="${v.dataset.mp4}" type="video/mp4">`;
    ($('picture', fig) || img).hidden = true; v.hidden = false; if (nota) nota.textContent = nota.dataset.notaVideo;
    v.load(); v.play().catch(() => {});
  };
  if (conVideo) {
    const figs = $$('video[data-video]').map(v => v.closest('figure'));
    const iov = 'IntersectionObserver' in window ? new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) { activar(e.target); iov.unobserve(e.target); } }), { rootMargin: '200px' }) : null;
    figs.forEach(f => iov ? iov.observe(f) : activar(f));
  }
  // consentimiento de cookies: sin consentimiento no se carga ninguna analítica (no hay proveedor configurado todavía)
  const KEY = 'ianalitica-consent', ck = $('#cookies');
  const get = () => { try { return localStorage.getItem(KEY); } catch { return null; } }, set = x => { try { localStorage.setItem(KEY, x); } catch {} };
  window.medir = ev => { if (get() === 'si') (window.dataLayer = window.dataLayer || []).push({ event: ev }); };
  if (ck && !get()) ck.classList.add('on');
  $('#ck-si')?.addEventListener('click', () => { set('si'); ck.classList.remove('on'); });
  $('#ck-no')?.addEventListener('click', () => { set('no'); ck.classList.remove('on'); });
  $$('[data-medir]').forEach(a => a.addEventListener('click', () => window.medir(a.dataset.medir)));
  // formulario de demo
  const f = $('#demo-form'); if (!f) return;
  const msg = $('#demo-msg'), val = {
    nombre: x => x.trim().length >= 3, cargo: x => x.trim().length >= 2, ips: x => x.trim().length >= 3,
    nit: x => /^[0-9]{6,10}(-?[0-9])?$/.test(x.trim()), ciudad: x => x.trim().length >= 2, sedes: x => /^[1-9][0-9]{0,2}$/.test(x.trim()),
    his: x => x !== '', correo: x => /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(x.trim()), telefono: x => /^[0-9+()\s-]{7,20}$/.test(x.trim()),
  };
  f.addEventListener('submit', async e => {
    e.preventDefault(); msg.className = 'msg'; $$('.err', f).forEach(x => x.remove());
    if ($('[name=sitio_web]', f).value) return;                 // honeypot: un humano no lo ve
    let ok = true;
    for (const [n, fn] of Object.entries(val)) { const el = f.elements[n]; if (!fn(el.value)) { ok = false; el.setAttribute('aria-invalid', 'true'); el.insertAdjacentHTML('afterend', '<div class="err" role="alert">Revise este dato.</div>'); } else el.removeAttribute('aria-invalid'); }
    if (!f.elements.autoriza.checked) { ok = false; f.elements.autoriza.closest('.chk').insertAdjacentHTML('afterend', '<div class="err" role="alert">Debe autorizar el tratamiento de sus datos para continuar.</div>'); }
    if (!ok) { msg.className = 'msg er'; msg.textContent = 'Hay datos por corregir.'; $('[aria-invalid=true]', f)?.focus(); return; }
    const d = Object.fromEntries(new FormData(f)); delete d.sitio_web; d.autoriza = true; d.comunicaciones = !!f.elements.comunicaciones.checked; d.autorizadoEn = new Date().toISOString(); d.version_politica = 'borrador-1';
    if (!C.endpointProspectos) {                                 // sin API todavía: se abre el correo comercial con los datos
      if (!correoOk) { msg.className = 'msg er'; msg.textContent = 'El formulario aún no está habilitado.'; return; }
      const cuerpo = Object.entries(d).map(([k, v]) => `${k}: ${v}`).join('\n');
      location.href = `mailto:${C.correoComercial}?subject=${encodeURIComponent('Solicitud de demo IAnalitica')}&body=${encodeURIComponent(cuerpo)}`;
      msg.className = 'msg ok'; msg.textContent = 'Abrimos su programa de correo con la solicitud lista para enviar.'; window.medir?.('demo_enviada'); return;
    }
    try {
      const r = await fetch(C.endpointProspectos, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(d) });
      if (!r.ok) throw new Error(r.status);
      f.reset(); msg.className = 'msg ok'; msg.textContent = 'Recibimos su solicitud. Le escribiremos en un día hábil para agendar el diagnóstico.'; window.medir?.('demo_enviada');
    } catch { msg.className = 'msg er'; msg.textContent = correoOk ? 'No pudimos enviar la solicitud. Intente de nuevo o escríbanos a ' + C.correoComercial + '.' : 'No pudimos enviar la solicitud. Intente de nuevo.'; }
  });
})();
