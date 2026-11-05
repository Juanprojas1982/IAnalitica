using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace IAnalitica.Infrastructure.Empresas;

internal sealed class RepositorioEmpresas(AnaliticaDbContext db) : IRepositorioEmpresas
{
    public Task<bool> ExisteNitAsync(string nit, CancellationToken ct)
        => db.Empresas.AsNoTracking().AnyAsync(e => e.Nit == nit, ct);

    public async Task<int> CrearAsync(EmpresaNueva n, CancellationToken ct)
    {
        var plan = await db.IaPlanes.AsNoTracking().FirstOrDefaultAsync(p => p.Codigo == n.PlanIa, ct)
                   ?? throw new InvalidOperationException($"El plan de IA '{n.PlanIa}' no existe (ia.PlanComercial).");
        var codigoModelo = await db.IaParametros.AsNoTracking().Where(p => p.Clave == "MODELO_DEFECTO").Select(p => p.Valor).FirstOrDefaultAsync(ct)
                           ?? throw new InvalidOperationException("Falta el parámetro ia.Parametro.MODELO_DEFECTO.");
        var modelo = await db.IaModelos.AsNoTracking().FirstOrDefaultAsync(m => m.Codigo == codigoModelo, ct)
                     ?? throw new InvalidOperationException($"El modelo por defecto '{codigoModelo}' no existe (ia.Modelo).");

        var empresa = new EmpresaEntidad
        {
            Codigo = n.Codigo, Nit = n.Nit, DigitoVerificacion = n.DigitoVerificacion, RazonSocial = n.RazonSocial,
            NombreComercial = n.NombreComercial, TipoEntidad = n.TipoEntidad, Naturaleza = n.Naturaleza, Ciudad = n.Ciudad,
            Departamento = n.Departamento, Telefono = n.Telefono, EmailContacto = n.EmailContacto, LogoUrl = n.LogoUrl,
            ColorPrimario = n.ColorPrimario, ColorSecundario = n.ColorSecundario,
            Modulos = n.Modulos.Select(m => new EmpresaModuloEntidad { Modulo = m }).ToList(),
            Contactos = n.Contactos.Select(c => new EmpresaContactoEntidad
            {
                Tipo = c.Tipo, Nombre = c.Nombre, Cargo = c.Cargo, Email = c.Email, Telefono = c.Telefono,
                EsPrincipal = c.EsPrincipal, RecibeAlertas = c.RecibeAlertas,
            }).ToList(),
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // La IA nace deshabilitada hasta el consentimiento (spec 22 §4).
        db.IaEmpresaConfigs.Add(new IaEmpresaConfigEntidad
        {
            EmpresaId = empresa.EmpresaId, Habilitado = false, PlanId = plan.PlanId, CupoIncluidoCop = n.CupoIaCop, ModeloPrincipalId = modelo.ModeloId,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return empresa.EmpresaId;
    }
}
