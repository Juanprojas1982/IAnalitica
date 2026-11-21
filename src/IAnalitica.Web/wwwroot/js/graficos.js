// Tooltips de los gráficos SVG (mejora progresiva; sin JS queda el <title> nativo y «Ver los datos»). Sin dependencias ni scripts en línea (CSP).
(function () {
    'use strict';
    var tip = null;

    function obtenerTip() {
        if (!tip) {
            tip = document.createElement('div');
            tip.className = 'tip';
            tip.setAttribute('aria-hidden', 'true');
            tip.hidden = true;
            document.body.appendChild(tip);
        }
        return tip;
    }

    function ocultar() { if (tip) tip.hidden = true; }

    // Sube desde el objetivo hasta el svg y devuelve el primer elemento con data-tip o con un <title> hijo (no el raíz del svg).
    function elementoConTip(objetivo) {
        if (!objetivo || !objetivo.closest) return null;
        var svg = objetivo.closest('figure.grafico svg');
        if (!svg) return null;
        var el = objetivo;
        while (el && el !== svg) {
            if (el.hasAttribute && el.hasAttribute('data-tip')) return el;
            for (var i = 0; i < el.children.length; i++) {
                if (el.children[i].localName === 'title') return el;
            }
            el = el.parentNode;
        }
        return null;
    }

    function textoDe(el) {
        var texto = el.getAttribute('data-tip');
        if (texto) return texto;
        for (var i = 0; i < el.children.length; i++) {
            var hijo = el.children[i];
            if (hijo.localName === 'title') {
                texto = hijo.textContent;
                el.setAttribute('data-tip', texto);
                el.setAttribute('aria-label', texto);   // el lector de pantalla conserva el texto
                el.removeChild(hijo);                   // evita el tooltip nativo además del propio
                return texto;
            }
        }
        return '';
    }

    function posicionar(e) {
        var t = obtenerTip();
        if (t.hidden) return;
        var x = e.clientX + 12, y = e.clientY + 12;
        if (x + t.offsetWidth > window.innerWidth) x = e.clientX - 12 - t.offsetWidth;
        if (y + t.offsetHeight > window.innerHeight) y = e.clientY - 12 - t.offsetHeight;
        t.style.left = Math.max(0, x + window.scrollX) + 'px';
        t.style.top = Math.max(0, y + window.scrollY) + 'px';
    }

    document.addEventListener('pointerover', function (e) {
        var el = elementoConTip(e.target);
        var texto = el ? textoDe(el) : '';
        if (!texto) { ocultar(); return; }   // un toque fuera de un elemento con tip lo oculta
        var t = obtenerTip();
        t.textContent = texto;
        t.hidden = false;
        posicionar(e);
    });
    document.addEventListener('pointermove', posicionar);
    document.addEventListener('pointerout', function (e) { if (elementoConTip(e.target)) ocultar(); });
    window.addEventListener('scroll', ocultar, true);
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') ocultar(); });
})();
