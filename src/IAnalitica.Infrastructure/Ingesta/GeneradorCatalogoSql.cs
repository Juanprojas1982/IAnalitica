using System.Text;

namespace IAnalitica.Infrastructure.Ingesta;

/// <summary>
/// Genera el lote T-SQL que procesa un catálogo: lee el JSON de <c>stg.Payload</c> con OPENJSON, valida por fila (requeridos,
/// valores permitidos, duplicados, referencias), registra los rechazos en <c>etl.Rechazo</c> y aplica el resto en una sola transacción
/// (upsert por llave natural; con COMPLETA desactiva lo ausente cuando la tabla tiene bandera de activo).
/// Parámetros: <c>@CargaId, @Emp, @Modo</c>; salidas: <c>@Ins, @Upd, @Del, @Rech</c>. Todo identificador sale de <see cref="DefinicionesCatalogo"/>.
/// </summary>
internal static class GeneradorCatalogoSql
{
    public static string Generar(DefCatalogo d)
    {
        var refs = d.Refs ?? Array.Empty<Ref>();
        var s = new StringBuilder();
        s.AppendLine("SET NOCOUNT, XACT_ABORT ON;");
        s.AppendLine("SELECT @Ins = 0, @Upd = 0, @Del = 0, @Rech = 0;");

        // staging
        s.Append("CREATE TABLE #s (Ord int IDENTITY(1,1) PRIMARY KEY, ").Append(Q(d.ClaveSql)).Append(' ').Append(EnTemporal(d.ClaveTipo)).Append(" NULL");
        foreach (var c in d.Cols) s.Append(", ").Append(Q(c.Sql)).Append(' ').Append(EnTemporal(c.Tipo)).Append(" NULL");
        foreach (var r in refs) s.Append(", ").Append(Q(r.Json)).Append(' ').Append(EnTemporal(r.TipoCodigo)).Append(" NULL, ").Append(Q(r.DestinoId)).Append(" int NULL");
        s.AppendLine(", Motivo nvarchar(400) COLLATE DATABASE_DEFAULT NULL);");

        var destinoCols = new List<string> { Q(d.ClaveSql) };
        destinoCols.AddRange(d.Cols.Select(c => Q(c.Sql)));
        destinoCols.AddRange(refs.Select(r => Q(r.Json)));
        s.Append("INSERT #s (").Append(string.Join(", ", destinoCols)).AppendLine(")");
        s.Append("SELECT ").Append(string.Join(", ", destinoCols.Select(c => "j." + c))).AppendLine();
        s.AppendLine("FROM stg.Payload p CROSS APPLY OPENJSON(p.Json) WITH (");
        var lectura = new List<string> { $"{Q(d.ClaveSql)} {d.ClaveTipo} '$.{d.ClaveJson}'" };
        lectura.AddRange(d.Cols.Select(c => $"{Q(c.Sql)} {c.Tipo} '$.{c.Json}'"));
        lectura.AddRange(refs.Select(r => $"{Q(r.Json)} {r.TipoCodigo} '$.{r.Json}'"));
        s.Append("    ").AppendLine(string.Join(", ", lectura));
        s.AppendLine(") j WHERE p.CargaId = @CargaId ORDER BY p.NumeroLote;");

        if (d.DespuesDeLeer is not null) s.AppendLine(d.DespuesDeLeer);

        // validación por fila
        s.AppendLine($"UPDATE #s SET Motivo = 'CAMPO_REQUERIDO:{d.ClaveJson}' WHERE Motivo IS NULL AND {Q(d.ClaveSql)} IS NULL;");
        foreach (var c in d.Cols.Where(c => c.Requerida))
            s.AppendLine($"UPDATE #s SET Motivo = 'CAMPO_REQUERIDO:{c.Json}' WHERE Motivo IS NULL AND {Q(c.Sql)} IS NULL;");
        foreach (var c in d.Cols.Where(c => c.Permitidos is not null))
        {
            var lista = string.Join(", ", c.Permitidos!.Select(v => $"'{v}'"));
            s.AppendLine($"UPDATE #s SET Motivo = 'VALOR_INVALIDO:{c.Json}' WHERE Motivo IS NULL AND {Q(c.Sql)} IS NOT NULL AND CAST({Q(c.Sql)} AS varchar(20)) NOT IN ({lista});");
        }
        s.AppendLine($"WITH d AS (SELECT Motivo, ROW_NUMBER() OVER (PARTITION BY {Q(d.ClaveSql)} ORDER BY Ord DESC) AS rn FROM #s WHERE Motivo IS NULL)");
        s.AppendLine("UPDATE d SET Motivo = 'DUPLICADO_EN_CARGA' WHERE rn > 1;");
        foreach (var r in refs)
        {
            s.AppendLine($"UPDATE s SET {Q(r.DestinoId)} = t.{Q(r.DimId)} FROM #s s JOIN {r.DimTabla} t ON t.EmpresaId = @Emp AND t.{Q(r.DimClave)} = s.{Q(r.Json)};");
            if (r.Requerida)
                s.AppendLine($"UPDATE #s SET Motivo = 'REFERENCIA_INEXISTENTE:{r.Json}' WHERE Motivo IS NULL AND {Q(r.DestinoId)} IS NULL;");
        }
        s.AppendLine("INSERT etl.Rechazo (CargaId, EmpresaId, SourceKey, Motivo) SELECT @CargaId, @Emp, " + Q(d.ClaveSql) + ", Motivo FROM #s WHERE Motivo IS NOT NULL;");
        s.AppendLine("SET @Rech = @@ROWCOUNT;");

        // aplicación
        string Valor(Col c) => c.Defecto is null ? $"s.{Q(c.Sql)}" : $"ISNULL(s.{Q(c.Sql)}, {c.Defecto})";
        s.AppendLine("BEGIN TRAN;");
        var sets = d.Cols.Select(c => $"t.{Q(c.Sql)} = {Valor(c)}").Concat(refs.Select(r => $"t.{Q(r.DestinoId)} = s.{Q(r.DestinoId)}"));
        s.AppendLine($"UPDATE t SET {string.Join(", ", sets)} FROM {d.Tabla} t JOIN #s s ON t.EmpresaId = @Emp AND t.{Q(d.ClaveSql)} = s.{Q(d.ClaveSql)} WHERE s.Motivo IS NULL;");
        s.AppendLine("SET @Upd = @@ROWCOUNT;");
        var colsIns = new[] { "EmpresaId", Q(d.ClaveSql) }.Concat(d.Cols.Select(c => Q(c.Sql))).Concat(refs.Select(r => Q(r.DestinoId)));
        var valsIns = new[] { "@Emp", $"s.{Q(d.ClaveSql)}" }.Concat(d.Cols.Select(Valor)).Concat(refs.Select(r => $"s.{Q(r.DestinoId)}"));
        s.AppendLine($"INSERT {d.Tabla} ({string.Join(", ", colsIns)}) SELECT {string.Join(", ", valsIns)} FROM #s s");
        s.AppendLine($"WHERE s.Motivo IS NULL AND NOT EXISTS (SELECT 1 FROM {d.Tabla} t WHERE t.EmpresaId = @Emp AND t.{Q(d.ClaveSql)} = s.{Q(d.ClaveSql)});");
        s.AppendLine("SET @Ins = @@ROWCOUNT;");
        if (d.ColActivo is not null)
        {
            // «Filas ausentes → inactivas» (contrato 05 §3). Una fila rechazada no cuenta como ausente: conserva su versión anterior.
            s.AppendLine("IF @Modo = 'COMPLETA'");
            s.AppendLine($"BEGIN UPDATE t SET {Q(d.ColActivo)} = 0 FROM {d.Tabla} t WHERE t.EmpresaId = @Emp AND t.{Q(d.ColActivo)} = 1");
            s.AppendLine($"  AND NOT EXISTS (SELECT 1 FROM #s s WHERE s.{Q(d.ClaveSql)} = t.{Q(d.ClaveSql)}); SET @Del = @@ROWCOUNT; END");
        }
        if (d.DespuesDeAplicar is not null) s.AppendLine(d.DespuesDeAplicar);
        s.AppendLine("COMMIT;");
        return s.ToString();
    }

    /// <summary>Las tablas temporales toman la intercalación de tempdb: se fuerza la de la base para comparar con las dimensiones.</summary>
    private static string EnTemporal(string tipo) => tipo.Contains("char") ? tipo + " COLLATE DATABASE_DEFAULT" : tipo;

    private static string Q(string identificador) => "[" + identificador + "]";
}
