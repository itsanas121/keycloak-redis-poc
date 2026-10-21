var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// US-3: no authentication — proves the API itself is up.
app.MapGet("/api/public", () => new
{
    message = "Hello from the public endpoint. No token needed.",
    time = DateTimeOffset.UtcNow
});

app.Run();
