using IAnalitica.Application;
using IAnalitica.Application.Semantica;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Observabilidad;
using IAnalitica.Application.Reportes;
using IAnalitica.Infrastructure.Reportes;
using IAnalitica.Modulos.Auditor;
using IAnalitica.Modulos.Ese;
using IAnalitica.Modulos.Farmacia;
using IAnalitica.Modulos.Financiero;
using IAnalitica.Modulos.Ingreso;
using IAnalitica.Modulos.Operacion;
using IAnalitica.Worker;

// Worker (24 §8): procesa etl.Trabajo. Las cargas y la cartera corren con rol_etl; los reportes (y luego la IA) corren con rol_app y el
// contexto del solicitante (§4.2). Por eso el worker tiene DOS cadenas de conexión.
var builder = Host.CreateApplicationBuilder(args);

var cadenaEtl = builder.Configuration.GetConnectionString("Etl")
                ?? throw new InvalidOperationException("Falta ConnectionStrings:Etl (usuario de rol_etl). Configúrela con User Secrets o variables de entorno.");
var cadenaApp = builder.Configuration.GetConnectionString("App")
                ?? throw new InvalidOperationException("Falta ConnectionStrings:App (usuario de rol_app). Los reportes se generan con la identidad de quien los pidió.");

builder.Services.AddObservabilidad("IAnalitica.Worker", builder.Configuration.GetSection("Observabilidad").Get<OpcionesObservabilidad>() ?? new());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddInfrastructure(cadenaApp);
builder.Services.AddApplication(ModuloFinanciero.Ensamblado, ModuloIngreso.Ensamblado, ModuloOperacion.Ensamblado,
    ModuloFarmacia.Ensamblado, ModuloEse.Ensamblado, ModuloAuditor.Ensamblado);
builder.Services.AddCapaSemantica(new[] { ModuloFinanciero.Modulo }.Concat(ModuloIngreso.Modulos));
builder.Services.AddProcesamiento(cadenaEtl);
builder.Services.AddReglasAuditor(ModuloAuditor.Reglas());
builder.Services.AddGeneracionReportes();
builder.Services.Configure<LimitesReportes>(builder.Configuration.GetSection("Reportes:Limites"));
builder.Services.Configure<OpcionesAlmacenReportes>(builder.Configuration.GetSection("Reportes:Almacen"));
builder.Services.AddSingleton(builder.Configuration.GetSection("Worker").Get<OpcionesWorker>() ?? new());
builder.Services.AddHostedService<ServicioTrabajos>();

builder.Build().Run();
