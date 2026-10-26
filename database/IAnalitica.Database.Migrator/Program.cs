using IAnalitica.Database.Migrator;

// Uso:
//   migrar --cadena "<cadena>" [--asegurar-bd]      aplica los scripts (la cadena también puede venir de IANALITICA_CONEXION)
//   crear-superadmin ...                             se agrega con Identity (F0)
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

    default:
        Console.Error.WriteLine($"Comando desconocido: {comando}");
        return 2;
}
