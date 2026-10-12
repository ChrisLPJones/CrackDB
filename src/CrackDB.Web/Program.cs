// CrackDB web host (skeleton). API, auth and database access arrive in a later phase.
var builder = WebApplication.CreateBuilder(args);

// Bind localhost only by default; override with --urls or ASPNETCORE_URLS.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5080");

var app = builder.Build();

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/", () => Results.Text("CrackDB web - coming soon."));

app.Run();
