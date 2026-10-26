using IAnalitica.Infrastructure.Observabilidad;

// Esqueleto de F0: la API de ingesta (token OpenIddict, /v1/cargas, lotes, cerrar, estado) se construye en F2 (24_SPEC_MAESTRA §13).
// Ya quedan listos la observabilidad con limpieza obligatoria y un chequeo de salud sin datos.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddObservabilidad("IAnalitica.Api", builder.Configuration.GetSection("Observabilidad").Get<OpcionesObservabilidad>() ?? new());
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseHttpsRedirection();

app.MapGet("/salud", () => Results.Ok(new { estado = "ok" }));

app.Run();

/// <summary>Necesario para las pruebas de integración (WebApplicationFactory).</summary>
public partial class Program;
