# Fuentes SaludIPS (metadatos, sin datos de pacientes)

| Archivo | Contenido |
|---|---|
| `diccionario_columnas.csv` | esquema;tabla;filas;orden;columna;tipo;longitud;nullable;es_pk de una instalación de SaludIPS |
| `diccionario_fk.csv` | tabla;columna;tabla_referenciada;columna_referenciada |
| `reportes1..4.csv` | código;nombre;SQL de los reportes configurados en SaludIPS (versiones de distintas instalaciones) |

Uso: referencia para las funciones `analitica.fn_*` del conector y para generar las tablas `saludips.*` (spec 16 §2).
Repositorio privado: los reportes contienen nombres y NIT de clientes escritos en el SQL.
