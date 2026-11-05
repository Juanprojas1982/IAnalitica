using System.Data;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Facturacion;
using IAnalitica.Domain.Facturacion;
using IAnalitica.Infrastructure.Correo;
using IAnalitica.Infrastructure.Ingesta;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IAnalitica.Infrastructure.Facturacion;

public sealed class OpcionesFacturacion
{
    /// <summary>URL pública del portal, sin barra final (enlaces de los correos). Ej.: https://app.midominio.com</summary>
    public string UrlPortal { get; set; } = "";
}

/// <summary>Falla transitoria al enviar: la cola reintenta con espera exponencial. El mensaje lleva solo códigos (jamás direcciones).</summary>
public sealed class CorreoTransitorioException(string codigo) : Exception("Envío de correo pendiente de reintento: " + codigo);

/// <summary>
/// PDF y correo de la cuenta de cobro (08 §32.4, §33). Corre con <c>rol_etl</c>. El envío es "al menos una vez": si el proceso muere entre la
/// aceptación del SMTP y la marca <c>ENVIADO</c>, el correo se duplica con el mismo Message-ID (se acepta). Nunca se registran direcciones ni textos.
/// </summary>
public sealed class EnviosFacturacion(IFabricaConexionesEtl etl, IGeneradorPdfCuentaCobro pdf, IAlmacenDocumentosFacturacion almacen, ICorreoSaliente correo,
    IOptions<OpcionesCorreoSmtp> smtp, IOptions<OpcionesFacturacion> opciones, ILogger<EnviosFacturacion> log)
{
    private sealed record FilaCuenta(int CuentaCobroId, int EmpresaId, int PeriodoKey, string Estado, string? Numero, decimal TotalCop, DateTime? FechaEmision,
        DateTime? FechaVencimiento, string? ClienteRazonSocial, string? ClienteNit, string? ClienteDv, string? ClienteDireccion, string? PdfRuta);
    private sealed record FilaLinea(string Concepto, int DiasFacturados, int DiasPeriodo, decimal ValorCop);
    private sealed record FilaParametro(string Clave, string Valor);
    private sealed record FilaDestinatario(string Clase, string Email, string? Nombre);
    private sealed record FilaEnvio(Guid EnvioId, int EmpresaId, int CuentaCobroId, string Tipo, string? Hito, bool ConAdjunto, string MessageId, string Estado, byte Intentos);
    private sealed record FilaConteo(string Codigo, int N);
    private sealed record FilaContacto(int ContactoId, string Tipo, string Nombre, string Email);

    // ------------------------------------------------------------------ PDF
    /// <summary>GENERAR_PDF_CUENTA_COBRO: genera y guarda el PDF una sola vez y encola el envío al cliente.</summary>
    public async Task GenerarPdfAsync(int cuentaCobroId, CancellationToken ct)
    {
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        var c = await CuentaAsync(cn, cuentaCobroId, ct).ConfigureAwait(false);
        if (c is null || c.Estado is not (EstadosCuentaCobro.Emitida or EstadosCuentaCobro.Pagada)) return;

        if (c.PdfRuta is null)
        {
            var p = (await cn.QueryAsync<FilaParametro>(new CommandDefinition("SELECT Clave, Valor FROM fac.Parametro WHERE Clave LIKE 'EMISOR_%' OR Clave = 'INSTRUCCIONES_PAGO'", cancellationToken: ct)).ConfigureAwait(false))
                .ToDictionary(x => x.Clave, x => x.Valor);
            var lineas = (await cn.QueryAsync<FilaLinea>(new CommandDefinition(
                "SELECT Concepto, DiasFacturados, DiasPeriodo, ValorCop FROM fac.CuentaCobroLinea WHERE CuentaCobroId = @cuentaCobroId ORDER BY Orden", new { cuentaCobroId }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
            var pendiente = await cn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT SUM(TotalCop - PagadoCop) FROM fac.CuentaCobro WHERE EmpresaId = @e AND Estado = 'EMITIDA' AND CuentaCobroId <> @cuentaCobroId", new { e = c.EmpresaId, cuentaCobroId }, cancellationToken: ct)).ConfigureAwait(false) ?? 0m;
            var instrucciones = p.GetValueOrDefault("INSTRUCCIONES_PAGO", "");

            var doc = new DocumentoCuentaCobro(
                new EmisorDoc(p.GetValueOrDefault("EMISOR_RAZON_SOCIAL", ""), p.GetValueOrDefault("EMISOR_NIT", ""), instrucciones),
                new ClienteDoc(c.ClienteRazonSocial ?? "", c.ClienteNit ?? "", c.ClienteDv, c.ClienteDireccion),
                c.Numero!, DateOnly.FromDateTime(c.FechaEmision!.Value), DateOnly.FromDateTime(c.FechaVencimiento!.Value), c.PeriodoKey,
                lineas.Select(l => new LineaDoc(l.Concepto, l.DiasFacturados, l.DiasPeriodo, l.ValorCop)).ToList(), Array.Empty<DetalleIaDoc>(),
                c.TotalCop, pendiente, instrucciones, Anulada: false);

            var bytes = pdf.Generar(doc);
            var ruta = await almacen.GuardarAsync(c.EmpresaId, c.Numero + ".pdf", bytes, ct).ConfigureAwait(false);
            await cn.ExecuteAsync(new CommandDefinition("UPDATE fac.CuentaCobro SET PdfRuta = @ruta, PdfSha256 = @sha WHERE CuentaCobroId = @cuentaCobroId AND PdfRuta IS NULL",
                new { ruta, sha = SHA256.HashData(bytes), cuentaCobroId }, cancellationToken: ct)).ConfigureAwait(false);
        }

        // Solo el primer envío lo crea el PDF; los reenvíos los pide el superadmin.
        var yaTiene = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM fac.Envio WHERE CuentaCobroId = @cuentaCobroId AND Tipo = 'CUENTA_COBRO' AND EsReenvio = 0", new { cuentaCobroId }, cancellationToken: ct)).ConfigureAwait(false);
        if (yaTiene == 0) await EncolarEnvioAsync(cuentaCobroId, "CUENTA_COBRO", null, esReenvio: false, usuarioId: null, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ outbox
    /// <summary>Crea <c>fac.Envio</c> + destinatarios + el trabajo ENVIAR_CORREO en una sola transacción. Sin contacto de facturación el envío nace FALLIDO con alerta.</summary>
    public async Task<Guid?> EncolarEnvioAsync(int cuentaCobroId, string tipo, string? hito, bool esReenvio, int? usuarioId, CancellationToken ct)
    {
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var c = await CuentaAsync(cn, cuentaCobroId, ct, tx).ConfigureAwait(false);
        if (c is null) return null;

        var contactos = (await cn.QueryAsync<FilaContacto>(new CommandDefinition("""
            SELECT TOP (5) ContactoId, Tipo, Nombre, Email FROM seg.EmpresaContacto
            WHERE EmpresaId = @e AND Tipo IN ('FACTURACION', 'CONTADOR') AND LEN(Email) > 0
            ORDER BY CASE Tipo WHEN 'FACTURACION' THEN 0 ELSE 1 END, EsPrincipal DESC, ContactoId
            """, new { e = c.EmpresaId }, tx, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var para = contactos.Where(x => x.Tipo == "FACTURACION").ToList();
        var copia = contactos.Where(x => x.Tipo == "CONTADOR").ToList();
        var conAdjunto = tipo == "CUENTA_COBRO";   // D42: solo la cuenta de cobro lleva adjunto

        var dominio = smtp.Value.RemitenteEmail is { } r && r.Contains('@') ? r[(r.IndexOf('@') + 1)..] : "ianalitica.local";
        var envioId = await cn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT fac.Envio (EmpresaId, CuentaCobroId, Tipo, Hito, EsReenvio, ConAdjunto, AdjuntoSha256, MessageId, Estado, CodigoError, SolicitadoPor)
            OUTPUT inserted.EnvioId
            SELECT @e, @cuentaCobroId, @tipo, @hito, @esReenvio, @conAdjunto, CASE WHEN @conAdjunto = 1 THEN PdfSha256 END, @messageId,
                   CASE WHEN @sinDestino = 1 THEN 'FALLIDO' ELSE 'PENDIENTE' END, CASE WHEN @sinDestino = 1 THEN 'SIN_DESTINATARIO' END, @usuarioId
            FROM fac.CuentaCobro WHERE CuentaCobroId = @cuentaCobroId
            """, new
        {
            e = c.EmpresaId, cuentaCobroId, tipo, hito, esReenvio, conAdjunto, usuarioId, sinDestino = para.Count == 0,
            messageId = $"fac-{cuentaCobroId}-{Guid.NewGuid():N}@{dominio}",
        }, tx, cancellationToken: ct)).ConfigureAwait(false);

        if (para.Count == 0)
        {
            await cn.ExecuteAsync(new CommandDefinition("""
                IF NOT EXISTS (SELECT 1 FROM fac.Alerta WHERE EmpresaId = @e AND PeriodoKey = @p AND Codigo = 'SIN_DESTINATARIO' AND AtendidaEn IS NULL)
                    INSERT fac.Alerta (EmpresaId, PeriodoKey, CuentaCobroId, Codigo) VALUES (@e, @p, @cuentaCobroId, 'SIN_DESTINATARIO')
                """, new { e = c.EmpresaId, p = c.PeriodoKey, cuentaCobroId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return envioId;
        }

        byte orden = 0;
        foreach (var d in para.Select(x => ("TO", x)).Concat(copia.Select(x => ("CC", x))))
            await cn.ExecuteAsync(new CommandDefinition(
                "INSERT fac.EnvioDestinatario (EnvioId, EmpresaId, Orden, Clase, ContactoId, Email, Nombre) VALUES (@envioId, @e, @orden, @clase, @contactoId, @email, @nombre)",
                new { envioId, e = c.EmpresaId, orden = ++orden, clase = d.Item1, contactoId = d.Item2.ContactoId, email = d.Item2.Email, nombre = d.Item2.Nombre }, tx, cancellationToken: ct)).ConfigureAwait(false);

        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT etl.Trabajo (Tipo, EmpresaId, ClaveExclusion, Prioridad, Payload)
            VALUES ('ENVIAR_CORREO', @e, @clave, 4, @payload)
            """, new { e = c.EmpresaId, clave = $"CORREO:{envioId}", payload = JsonSerializer.Serialize(new { envioId }) }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return envioId;
    }

    // ------------------------------------------------------------------ envío
    /// <summary>ENVIAR_CORREO. Transitoria → <see cref="CorreoTransitorioException"/> para que la cola reintente; permanente o último intento → FALLIDO + alerta.</summary>
    public async Task EnviarAsync(Guid envioId, bool esUltimoIntento, CancellationToken ct)
    {
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        var e = await cn.QueryFirstOrDefaultAsync<FilaEnvio>(new CommandDefinition(
            "SELECT EnvioId, EmpresaId, CuentaCobroId, Tipo, Hito, ConAdjunto, MessageId, Estado, Intentos FROM fac.Envio WHERE EnvioId = @envioId", new { envioId }, cancellationToken: ct)).ConfigureAwait(false);
        if (e is null || e.Estado is "ENVIADO" or "CANCELADO") return;   // ya enviado: no se reenvía

        var c = await CuentaAsync(cn, e.CuentaCobroId, ct).ConfigureAwait(false);
        if (c is null) return;
        await cn.ExecuteAsync(new CommandDefinition("UPDATE fac.Envio SET Estado = 'ENVIANDO', Intentos = Intentos + 1, UltimoIntentoEn = SYSUTCDATETIME() WHERE EnvioId = @envioId",
            new { envioId }, cancellationToken: ct)).ConfigureAwait(false);

        var dest = (await cn.QueryAsync<FilaDestinatario>(new CommandDefinition(
            "SELECT Clase, Email, Nombre FROM fac.EnvioDestinatario WHERE EnvioId = @envioId ORDER BY Orden", new { envioId }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var adjuntos = new List<AdjuntoCorreo>();
        if (e.ConAdjunto)
        {
            await using var s = c.PdfRuta is null ? null : await almacen.AbrirAsync(c.PdfRuta, ct).ConfigureAwait(false);
            if (s is null) { await FallarAsync(cn, e, "ADJUNTO_NO_ENCONTRADO", null, Alertas.EnvioFallido, ct).ConfigureAwait(false); return; }
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms, ct).ConfigureAwait(false);
            adjuntos.Add(new AdjuntoCorreo($"{c.Numero}.pdf", "application/pdf", ms.ToArray()));
        }

        var instrucciones = await cn.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT Valor FROM fac.Parametro WHERE Clave = 'INSTRUCCIONES_PAGO'", cancellationToken: ct)).ConfigureAwait(false) ?? "";
        var (asunto, texto, html) = Redactar(e, c, instrucciones, opciones.Value.UrlPortal);
        var salida = new CorreoSaliente(e.MessageId,
            dest.Where(x => x.Clase == "TO").Select(x => new DireccionCorreo(x.Email, x.Nombre)).ToList(),
            dest.Where(x => x.Clase == "CC").Select(x => new DireccionCorreo(x.Email, x.Nombre)).ToList(), asunto, texto, html, adjuntos);

        var r = await correo.EnviarAsync(salida, ct).ConfigureAwait(false);
        switch (r.Resultado)
        {
            case ResultadoCorreo.Aceptado:
                await cn.ExecuteAsync(new CommandDefinition("UPDATE fac.Envio SET Estado = 'ENVIADO', EnviadoEn = SYSUTCDATETIME(), CodigoSmtp = NULL, CodigoError = NULL WHERE EnvioId = @envioId", new { envioId }, cancellationToken: ct)).ConfigureAwait(false);
                log.LogInformation("Cuenta de cobro enviada {EnvioId} ({Tipo})", envioId, e.Tipo);
                break;
            case ResultadoCorreo.FallaTransitoria when !esUltimoIntento:
                await cn.ExecuteAsync(new CommandDefinition("UPDATE fac.Envio SET Estado = 'PENDIENTE', CodigoSmtp = @s, CodigoError = @c WHERE EnvioId = @envioId", new { envioId, s = r.CodigoSmtp, c = r.CodigoError }, cancellationToken: ct)).ConfigureAwait(false);
                throw new CorreoTransitorioException(r.CodigoError ?? "TRANSITORIA");
            case ResultadoCorreo.NoConfigurado:
                await FallarAsync(cn, e, "SMTP_NO_CONFIGURADO", null, Alertas.SmtpNoConfigurado, ct).ConfigureAwait(false);
                break;
            default:
                await FallarAsync(cn, e, r.CodigoError ?? "DESCONOCIDO", r.CodigoSmtp, Alertas.EnvioFallido, ct).ConfigureAwait(false);
                break;
        }
    }

    private static async Task FallarAsync(SqlConnection cn, FilaEnvio e, string codigoError, string? codigoSmtp, string alerta, CancellationToken ct)
    {
        await cn.ExecuteAsync(new CommandDefinition("UPDATE fac.Envio SET Estado = 'FALLIDO', CodigoError = @codigoError, CodigoSmtp = @codigoSmtp WHERE EnvioId = @EnvioId",
            new { e.EnvioId, codigoError, codigoSmtp }, cancellationToken: ct)).ConfigureAwait(false);
        // SMTP sin configurar es una alerta global (EmpresaId NULL); el resto, de la empresa.
        var global = alerta == Alertas.SmtpNoConfigurado;
        await cn.ExecuteAsync(new CommandDefinition("""
            IF NOT EXISTS (SELECT 1 FROM fac.Alerta WHERE ISNULL(EmpresaId, 0) = @emp AND PeriodoKey IS NULL AND Codigo = @alerta AND AtendidaEn IS NULL)
                INSERT fac.Alerta (EmpresaId, CuentaCobroId, Codigo) VALUES (NULLIF(@emp, 0), @cuenta, @alerta)
            """, new { emp = global ? 0 : e.EmpresaId, cuenta = global ? (int?)null : e.CuentaCobroId, alerta }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task<FilaCuenta?> CuentaAsync(SqlConnection cn, int cuentaCobroId, CancellationToken ct, IDbTransaction? tx = null)
        => await cn.QueryFirstOrDefaultAsync<FilaCuenta>(new CommandDefinition("""
            SELECT CuentaCobroId, EmpresaId, PeriodoKey, Estado, Numero, TotalCop, FechaEmision, FechaVencimiento, ClienteRazonSocial, ClienteNit, ClienteDv, ClienteDireccion, PdfRuta
            FROM fac.CuentaCobro WHERE CuentaCobroId = @cuentaCobroId
            """, new { cuentaCobroId }, tx, cancellationToken: ct)).ConfigureAwait(false);

    private static readonly CultureInfo Co = new("es-CO");
    private static readonly string[] MesesCortos = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"];

    /// <summary>Asunto y cuerpo (texto y HTML simple, sin imágenes remotas). Los recordatorios, avisos de mora y anulaciones van sin adjunto.</summary>
    private static (string Asunto, string Texto, string Html) Redactar(FilaEnvio e, FilaCuenta c, string instrucciones, string urlPortal)
    {
        var periodo = $"{MesesCortos[c.PeriodoKey % 100 - 1]}-{c.PeriodoKey / 100}";
        var total = "$ " + c.TotalCop.ToString("N0", Co);
        var vence = c.FechaVencimiento!.Value.ToString("dd/MM/yyyy", Co);
        var enlace = string.IsNullOrWhiteSpace(urlPortal) ? "" : $"{urlPortal.TrimEnd('/')}/facturacion/{c.CuentaCobroId}";
        var (asunto, intro) = e.Tipo switch
        {
            "RECORDATORIO" => ($"Recordatorio: cuenta de cobro {c.Numero} vence el {vence}", $"Le recordamos que la cuenta de cobro {c.Numero} por {total} vence el {vence}."),
            "AVISO_MORA" => ($"Cuenta de cobro {c.Numero} vencida", $"La cuenta de cobro {c.Numero} por {total} venció el {vence} y aún registra saldo pendiente."),
            "ANULACION" => ($"Cuenta de cobro {c.Numero} anulada", $"La cuenta de cobro {c.Numero} fue anulada. Si corresponde, recibirá una nueva cuenta de cobro que la reemplaza."),
            _ => ($"Cuenta de cobro {c.Numero} · {c.ClienteRazonSocial} · {periodo}", $"Adjuntamos la cuenta de cobro {c.Numero} correspondiente a {periodo}, por {total}, con vencimiento el {vence}."),
        };
        var pie = "Este correo se envía de forma automática. La cuenta de cobro es un documento no tributario. Nunca cambiamos datos bancarios por correo.";
        var texto = $"{intro}\n\n{(enlace.Length > 0 ? $"Consúltela en: {enlace}\n\n" : "")}Instrucciones de pago:\n{instrucciones}\n\n{pie}\n";
        var html = $"<p>{Esc(intro)}</p>{(enlace.Length > 0 ? $"<p><a href=\"{Esc(enlace)}\">Ver la cuenta de cobro en el portal</a></p>" : "")}"
                 + $"<p><b>Instrucciones de pago</b><br>{Esc(instrucciones).Replace("\n", "<br>")}</p><p style=\"color:#555;font-size:12px\">{Esc(pie)}</p>";
        return (asunto, texto, html);
    }

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);

    // ------------------------------------------------------------------ resumen diario de alertas (sin datos personales)
    /// <summary>RESUMEN_ALERTAS: si hay alertas abiertas, un correo a <c>CORREO_OPERACION</c> con conteos por código y enlace. Sin nombres, correos ni montos.</summary>
    public async Task<bool> EnviarResumenAlertasAsync(CancellationToken ct)
    {
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        var destino = await cn.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT Valor FROM fac.Parametro WHERE Clave = 'CORREO_OPERACION'", cancellationToken: ct)).ConfigureAwait(false);
        if (!MailAddress.TryCreate(destino, out var direccion)) return false;   // sin buzón configurado (marcador [BUZÓN DEL PRESTADOR]) no hay a quién avisar
        var conteos = (await cn.QueryAsync<FilaConteo>(new CommandDefinition(
            "SELECT Codigo, COUNT(*) AS N FROM fac.Alerta WHERE AtendidaEn IS NULL GROUP BY Codigo ORDER BY Codigo", cancellationToken: ct)).ConfigureAwait(false)).ToList();
        if (conteos.Count == 0) return false;

        var enlace = string.IsNullOrWhiteSpace(opciones.Value.UrlPortal) ? "" : $"{opciones.Value.UrlPortal.TrimEnd('/')}/admin/facturacion";
        var lista = string.Join("\n", conteos.Select(x => $"- {x.Codigo}: {x.N}"));
        var texto = $"Hay alertas de facturación abiertas:\n\n{lista}\n\n{(enlace.Length > 0 ? "Revíselas en: " + enlace : "")}\n";
        var r = await correo.EnviarAsync(new CorreoSaliente($"fac-resumen-{Guid.NewGuid():N}@ianalitica.local", [new(direccion.Address, null)], [],
            "Alertas de facturación abiertas", texto, null, []), ct).ConfigureAwait(false);
        if (r.Resultado == ResultadoCorreo.FallaTransitoria) throw new CorreoTransitorioException(r.CodigoError ?? "TRANSITORIA");
        return r.Resultado == ResultadoCorreo.Aceptado;
    }
}

internal sealed class ManejadorGenerarPdfCuentaCobro(EnviosFacturacion envios) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.GenerarPdfCuentaCobro;
    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(trabajo.Payload ?? throw new InvalidOperationException("El trabajo no trae payload."));
        await envios.GenerarPdfAsync(doc.RootElement.GetProperty("cuentaCobroId").GetInt32(), ct).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
}

internal sealed class ManejadorEnviarCorreo(EnviosFacturacion envios) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.EnviarCorreo;
    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(trabajo.Payload ?? throw new InvalidOperationException("El trabajo no trae payload."));
        await envios.EnviarAsync(doc.RootElement.GetProperty("envioId").GetGuid(), trabajo.EsUltimoIntento, ct).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
}

internal sealed class ManejadorResumenAlertas(EnviosFacturacion envios) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.ResumenAlertas;
    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        await envios.EnviarResumenAlertasAsync(ct).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
}
