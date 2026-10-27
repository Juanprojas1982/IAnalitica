using System.Data.Common;
using System.Text;
using Dapper;

namespace IAnalitica.Infrastructure.Ingesta;

/// <summary>
/// Entidad <c>configuracion</c> (FOTO, spec 16): reemplaza la copia de la configuración de SaludIPS de la empresa por la foto recibida.
/// Las filas llegan como <c>{tabla, clave, datos}</c>; cada tabla se inserta en <c>saludips.&lt;esquema&gt;_&lt;tabla&gt;</c>. La lista blanca es
/// el propio catálogo de la base (las tablas que existen en el esquema saludips): una tabla desconocida se rechaza, nunca se crea ni se concatena
/// texto de la solicitud al SQL. Las columnas se leen del catálogo, no de la fila.
/// </summary>
internal static class ProcesadorConfiguracion
{
    public static async Task<(int Ins, int Rech)> AplicarAsync(DbConnection cn, long cargaId, int empresaId, CancellationToken ct)
    {
        var meta = (await cn.QueryAsync<(string Tabla, string Columna, bool EsPk)>(new CommandDefinition("""
            SELECT t.name AS Tabla, c.name AS Columna, CAST(CASE WHEN ic.column_id IS NULL THEN 0 ELSE 1 END AS bit) AS EsPk
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id AND s.name = 'saludips'
            JOIN sys.columns c ON c.object_id = t.object_id AND c.name NOT IN ('EmpresaId', 'CargaId')
            LEFT JOIN sys.indexes i ON i.object_id = t.object_id AND i.is_primary_key = 1
            LEFT JOIN sys.index_columns ic ON ic.object_id = t.object_id AND ic.index_id = i.index_id AND ic.column_id = c.column_id
            ORDER BY t.name, c.column_id
            """, cancellationToken: ct)).ConfigureAwait(false))
            .GroupBy(x => x.Tabla).ToDictionary(g => g.Key, g => g.ToList());

        await using var tx = await cn.BeginTransactionAsync(ct).ConfigureAwait(false);
        // La foto reemplaza todo lo de la empresa (también las tablas que ya no vengan)
        foreach (var t in meta.Keys)
            await cn.ExecuteAsync(new CommandDefinition($"DELETE saludips.[{t}] WHERE EmpresaId = @empresaId", new { empresaId }, tx, commandTimeout: 600, cancellationToken: ct)).ConfigureAwait(false);

        // Filas por tabla (el nombre «esquema.tabla» del origen se vuelve «esquema_tabla»)
        var recibidas = (await cn.QueryAsync<(string Tabla, int Filas)>(new CommandDefinition("""
            SELECT REPLACE(j.tabla, '.', '_') AS Tabla, COUNT(*) AS Filas
            FROM stg.Payload p CROSS APPLY OPENJSON(p.Json) f CROSS APPLY OPENJSON(f.value) WITH (tabla nvarchar(128) '$.tabla') j
            WHERE p.CargaId = @cargaId GROUP BY REPLACE(j.tabla, '.', '_')
            """, new { cargaId }, tx, cancellationToken: ct)).ConfigureAwait(false)).ToDictionary(x => x.Tabla, x => x.Filas);

        int ins = 0, rech = 0;
        foreach (var (tabla, filas) in recibidas)
        {
            if (!meta.TryGetValue(tabla, out var columnas))
            {
                await cn.ExecuteAsync(new CommandDefinition("INSERT etl.Rechazo (CargaId, EmpresaId, SourceKey, Motivo) VALUES (@cargaId, @empresaId, @tabla, 'TABLA_NO_PERMITIDA')",
                    new { cargaId, empresaId, tabla = tabla[..Math.Min(tabla.Length, 100)] }, tx, cancellationToken: ct)).ConfigureAwait(false);
                rech += filas;
                continue;
            }
            var nombres = columnas.Select(c => $"[{c.Columna}]").ToList();
            var pk = columnas.Where(c => c.EsPk).Select(c => $"[{c.Columna}]").ToList();
            var sql = new StringBuilder();
            sql.Append($"INSERT saludips.[{tabla}] (EmpresaId, CargaId, {string.Join(", ", nombres)}) ");
            sql.Append($"SELECT @empresaId, @cargaId, {string.Join(", ", nombres)} FROM (");
            sql.Append($"SELECT {string.Join(", ", nombres.Select(n => "d." + n))}, ");
            sql.Append(pk.Count > 0
                ? $"ROW_NUMBER() OVER (PARTITION BY {string.Join(", ", pk.Select(n => "d." + n))} ORDER BY p.NumeroLote DESC, CAST(f.[key] AS int) DESC) AS rn "
                : "1 AS rn ");
            sql.Append("FROM stg.Payload p CROSS APPLY OPENJSON(p.Json) f CROSS APPLY OPENJSON(f.value) WITH (tabla nvarchar(128) '$.tabla', datos nvarchar(max) '$.datos' AS JSON) j ");
            sql.Append($"CROSS APPLY OPENJSON(j.datos) WITH ({string.Join(", ", columnas.Select(c => $"[{c.Columna}] nvarchar(max) '$.{c.Columna}'"))}) d ");
            sql.Append("WHERE p.CargaId = @cargaId AND REPLACE(j.tabla, '.', '_') = @tabla ");
            if (pk.Count > 0) sql.Append($"AND {string.Join(" AND ", pk.Select(n => "d." + n + " IS NOT NULL"))} ");
            sql.Append(") x WHERE rn = 1");
            var n = await cn.ExecuteAsync(new CommandDefinition(sql.ToString(), new { empresaId, cargaId, tabla }, tx, commandTimeout: 600, cancellationToken: ct)).ConfigureAwait(false);
            ins += n;
            if (n < filas)
            {
                await cn.ExecuteAsync(new CommandDefinition("INSERT etl.Rechazo (CargaId, EmpresaId, SourceKey, Motivo) VALUES (@cargaId, @empresaId, @tabla, @motivo)",
                    new { cargaId, empresaId, tabla, motivo = $"FILAS_SIN_LLAVE_O_REPETIDAS:{filas - n}" }, tx, cancellationToken: ct)).ConfigureAwait(false);
                rech += filas - n;
            }
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (ins, rech);
    }
}
