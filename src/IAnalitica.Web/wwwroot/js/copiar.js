// Botones «Copiar» del kit de entrega (sin scripts en línea por el CSP). Cualquier elemento con data-copiar="idDelElemento".
document.addEventListener('click', function (e) {
    var boton = e.target.closest('[data-copiar]');
    if (!boton) return;
    var origen = document.getElementById(boton.getAttribute('data-copiar'));
    if (origen && navigator.clipboard) {
        navigator.clipboard.writeText(origen.innerText.trim()).then(function () {
            var texto = boton.innerText;
            boton.innerText = 'Copiado';
            setTimeout(function () { boton.innerText = texto; }, 1500);
        });
    }
});
