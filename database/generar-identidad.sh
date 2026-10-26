#!/usr/bin/env bash
# Genera database/scripts/0005_identidad.sql a partir de la migración de IdentidadDbContext (24_SPEC_MAESTRA §6.2).
# DbUp lleva su propio registro de versiones, así que se quita la tabla __EFMigrationsHistory del script de EF.
# Uso:  database/generar-identidad.sh [desde] [hasta]     (por omisión 0 → última migración)
# Un cambio posterior de Identity = nueva migración + script incremental con número 1000 o mayor (nunca se edita el 0005).
set -euo pipefail
cd "$(dirname "$0")/.."
desde="${1:-0}"; hasta="${2:-}"
salida="${3:-database/scripts/0005_identidad.sql}"
tmp="$(mktemp)"
dotnet ef migrations script $desde $hasta --context IdentidadDbContext --project src/IAnalitica.Infrastructure -o "$tmp" >/dev/null
python3 - "$tmp" "$salida" <<'PY'
import re, sys
s = open(sys.argv[1], encoding='utf-8-sig').read()
s = re.sub(r"IF OBJECT_ID\(N'\[__EFMigrationsHistory\]'\) IS NULL\s*BEGIN.*?END;\s*GO\s*", "", s, flags=re.S)
s = re.sub(r"INSERT INTO \[__EFMigrationsHistory\].*?;\s*", "", s, flags=re.S)
s = "/* 0005 · Identity + OpenIddict en el esquema seg. GENERADO por database/generar-identidad.sh: no editar a mano. */\n" + s
open(sys.argv[2], 'w', encoding='utf-8').write(s)
PY
rm -f "$tmp"
