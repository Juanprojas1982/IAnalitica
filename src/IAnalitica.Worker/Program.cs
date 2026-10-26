using IAnalitica.Infrastructure.Observabilidad;
using IAnalitica.Worker;

// Esqueleto de F0: el consumo de etl.Trabajo (PROCESAR_CARGA, CALCULAR_CARTERA...) llega en F2 (24_SPEC_MAESTRA §8, §13).
// El worker procesará con una cadena de rol_etl y los reportes/IA con rol_app y el contexto del solicitante (§4.2).
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddObservabilidad("IAnalitica.Worker", builder.Configuration.GetSection("Observabilidad").Get<OpcionesObservabilidad>() ?? new());
builder.Services.AddHostedService<Latido>();

builder.Build().Run();
