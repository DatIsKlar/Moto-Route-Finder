using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MotoRouteFinder.Models;
using MotoRouteFinder.Server.Services;
using MotoRouteFinder.Services;

var builder = WebApplication.CreateBuilder(args);

const long MaxUploadSize = 1073741824; // 1 GB

var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
// Default to localhost for security; set HOST=0.0.0.0 to expose on all interfaces
var host = Environment.GetEnvironmentVariable("HOST") ?? "127.0.0.1";
builder.WebHost.UseUrls($"http://{host}:{port}");
builder.WebHost.ConfigureKestrel(options =>
{
    var maxUploadBytes = builder.Configuration.GetValue<long>("MaxUploadSizeBytes", MaxUploadSize);
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = builder.Configuration.GetValue<long>("MaxUploadSizeBytes", MaxUploadSize);
});

builder.Services.Configure<RouteGenerationOptions>(builder.Configuration.GetSection("RouteGeneration"));

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddSingleton<RoutingService>(sp =>
    new RoutingService(
        sp.GetRequiredService<IOptions<RouteGenerationOptions>>(),
        sp.GetRequiredService<ILogger<RoutingService>>()));
builder.Services.AddSingleton<SavedMapsService>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<JobProgressStore>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.MapGet("/", () => Results.Redirect("/index.html"));

app.Run();
