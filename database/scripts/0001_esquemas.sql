/* 0001 · Esquemas. Primero de todos: Identity (0005) crea sus tablas en seg. Idempotente. */
DECLARE @e TABLE (Nombre sysname);
INSERT @e VALUES ('seg'),('cfg'),('etl'),('stg'),('dim'),('fact'),('rpt'),('aud'),('ia'),('reg'),('saludips'),('crm');
DECLARE @n sysname, @sql nvarchar(200);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Nombre FROM @e;
OPEN c; FETCH NEXT FROM c INTO @n;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF SCHEMA_ID(@n) IS NULL BEGIN SET @sql = N'CREATE SCHEMA ' + QUOTENAME(@n); EXEC (@sql); END;
    FETCH NEXT FROM c INTO @n;
END;
CLOSE c; DEALLOCATE c;
