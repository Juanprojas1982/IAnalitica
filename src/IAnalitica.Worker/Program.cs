using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Observabilidad;
using IAnalitica.Worker;

// Worker (24 §8): procesa etl.Trabajo con el login de rol_etl (cargas, cartera). Los reportes y la IA correrán con rol_app y el
// contexto del solicitante (§4.2) cuando existan (F3/F5): por eso el worker tiene DOS cadenas de conexión.
var builder = Host.CreateApplicationBuilder(args);

var cadenaEtl = builder.Configuration.GetConnectionString("Etl")
                ?? throw new InvalidOperationException("Falta ConnectionStrings:Etl (usuario de rol_etl). Configúrela con User Secrets o variables de entorno.");

builder.Services.AddObservabilidad("IAnalitica.Worker", builder.Configuration.GetSection("Observabilidad").Get<OpcionesObservabilidad>() ?? new());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProcesamiento(cadenaEtl);
builder.Services.AddSingleton(builder.Configuration.GetSection("Worker").Get<OpcionesWorker>() ?? new());
builder.Services.AddHostedService<ServicioTrabajos>();

builder.Build().Run();
