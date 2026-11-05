#!/usr/bin/env python3
"""Genera database/scripts/1000_saludips_configuracion.sql: la copia por empresa de las tablas de configuración de SaludIPS
(spec 16 §2, 0090). Entradas:
  - docs/fuentes-saludips/diccionario_columnas.csv   esquema;tabla;filas;orden;columna;tipo;longitud;nullable;es_pk
  - database/Herramientas/configuracion_columnas.tsv  tabla -> columnas que el conector SÍ envía (salen de analitica.fn_Configuracion, F1)
Una tabla saludips.<esquema>_<tabla> por cada fila del TSV, con las mismas columnas más EmpresaId y CargaId; PK = (EmpresaId, PK original).
Si cambia la lista blanca del conector, se regenera el TSV y se vuelve a ejecutar este script (y se agrega un script nuevo, no se edita el 1000).
Uso:  python3 database/generar-saludips.py"""
import collections, pathlib

raiz = pathlib.Path(__file__).resolve().parent.parent
dic = collections.defaultdict(dict)
for linea in (raiz / "docs/fuentes-saludips/diccionario_columnas.csv").read_text(encoding="utf-8-sig").splitlines():
    p = linea.split(";")
    if len(p) >= 9:
        dic[f"{p[0]}.{p[1]}"][p[4]] = (int(p[3]), p[5].lower(), int(p[6]), p[7] == "1", p[8] == "1")

def tipo(nombre, largo):
    if nombre in ("int", "smallint", "tinyint", "bigint", "bit", "float", "real", "date", "datetime", "smalldatetime", "datetime2"): return nombre
    if nombre in ("numeric", "decimal"): return "decimal(19,6)"       # el diccionario no trae precisión ni escala: holgado
    if nombre == "varchar": return "varchar(max)" if largo in (-1, 0) or largo > 8000 else f"varchar({largo})"
    if nombre == "nvarchar": return "nvarchar(max)" if largo in (-1, 0) or largo > 4000 else f"nvarchar({largo})"
    raise SystemExit(f"Tipo sin mapeo: {nombre}")

sal = ["/* 1000 · Copia por empresa de la configuración de SaludIPS (spec 16 §2). GENERADO por database/generar-saludips.py: no editar a mano.",
       "   Una tabla por cada tabla de la lista blanca del conector. EmpresaId y CargaId se agregan; la RLS (siempre/9000) las cubre sola. */"]
tablas = []
for fila in (raiz / "database/Herramientas/configuracion_columnas.tsv").read_text(encoding="utf-8").splitlines():
    if not fila.strip(): continue
    t, cols = fila.split("\t"); cols = cols.split(",")
    if t not in dic: raise SystemExit(f"{t} no está en el diccionario")
    destino = "saludips." + t.replace(".", "_")
    defs, pk = [], []
    for c in cols:
        if c not in dic[t]: raise SystemExit(f"{t}.{c} no está en el diccionario")
        _, ty, largo, nulo, es_pk = dic[t][c]
        if es_pk: pk.append(c)
        defs.append(f"    [{c}] {tipo(ty, largo)} {'NOT NULL' if es_pk else 'NULL'}")
    sal.append(f"CREATE TABLE {destino} (")
    sal.append("    EmpresaId int NOT NULL CONSTRAINT FK_" + destino[9:] + "_Empresa REFERENCES seg.Empresa,")
    sal.append("    CargaId bigint NOT NULL,")
    sal.append(",\n".join(defs) + ("," if pk else ""))
    if pk: sal.append("    CONSTRAINT PK_" + destino[9:] + " PRIMARY KEY (EmpresaId, " + ", ".join(f"[{c}]" for c in pk) + ")")
    else: sal[-1] = sal[-1].rstrip(",")
    sal.append(");")
    if not pk: sal.append(f"CREATE INDEX IX_{destino[9:]}_Empresa ON {destino} (EmpresaId);")
    sal.append("GO")
    tablas.append((t, destino, [c for c in cols], pk))
sal.append(f"-- {len(tablas)} tablas")
(raiz / "database/scripts/1000_saludips_configuracion.sql").write_text("\n".join(sal) + "\n", encoding="utf-8")
print(len(tablas), "tablas;", sum(1 for t in tablas if not t[3]), "sin PK")
