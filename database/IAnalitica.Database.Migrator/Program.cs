using IAnalitica.Database.Migrator;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Identity;
using IAnalitica.Application;
using Microsoft.Extensions.DependencyInjection;

// Uso:
//   migrar --cadena "<cadena>" [--asegurar-bd]      aplica los scripts (la cadena también puede venir de IANALITICA_CONEXION)
//   crear-superadmin --usuario <u> --correo <c>     crea el primer superadmin (falla si ya hay uno); muestra la clave UNA vez
// La cadena de conexión nunca se escribe en archivos del repositorio (§4.4).
var argumentos = args.ToList();

string? Opcion(string nombre)
{
    var i = argumentos.IndexOf(nombre);
    return i >= 0 && i + 1 < argumentos.Count ? argumentos[i + 1] : null;
}

var comando = argumentos.FirstOrDefault() ?? "migrar";
var cadena = Opcion("--cadena") ?? Environment.GetEnvironmentVariable("IANALITICA_CONEXION");
if (string.IsNullOrWhiteSpace(cadena))
{
    Console.Error.WriteLine("Falta la cadena de conexión: use --cadena o la variable IANALITICA_CONEXION.");
    return 2;
}

switch (comando)
{
    case "migrar":
        var resultado = Migrador.Ejecutar(cadena, argumentos.Contains("--asegurar-bd"), Console.Out);
        Console.WriteLine(resultado.Exitoso
            ? $"Migración completa ({resultado.ScriptsEjecutados} scripts ejecutados)."
            : $"Migración fallida: {resultado.Error}");
        return resultado.Exitoso ? 0 : 1;

    case "crear-superadmin":
        var servicios = new ServiceCollection().AddLogging().AddInfrastructure(cadena).BuildServiceProvider();
        var superadmin = await CrearSuperadmin.EjecutarAsync(servicios, Opcion("--usuario") ?? "", Opcion("--correo") ?? "", CancellationToken.None);
        if (!superadmin.EsExitoso)
        {
            Console.Error.WriteLine("No se creó el superadmin: " + string.Join("; ", superadmin.Errores.Select(e => e.Mensaje)));
            return 1;
        }
        var datos = superadmin.Valor;
        Console.WriteLine($"Usuario: {datos.Usuario}");
        Console.WriteLine($"Contraseña inicial (se muestra una sola vez): {datos.ClaveInicial}");
        Console.WriteLine($"Vence: {datos.ClaveVenceEnUtc:yyyy-MM-dd HH:mm} UTC. En el primer ingreso debe cambiarla y configurar el doble factor.");
        return 0;

    default:
        Console.Error.WriteLine($"Comando desconocido: {comando}");
        return 2;
}
