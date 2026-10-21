using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var keycloak = builder.Configuration.GetSection("Keycloak");

// US-4: validate Keycloak access tokens locally. On first use the handler downloads the
// realm's public signing keys from {Authority}/.well-known/openid-configuration and caches
// them; after that, tokens are checked without calling Keycloak.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = keycloak["Authority"];
        options.Audience = keycloak["Audience"];
        options.RequireHttpsMetadata = false; // local lab only: Keycloak runs on plain http
        options.MapInboundClaims = false;     // keep Keycloak's claim names (preferred_username, ...)
        options.TokenValidationParameters = new()
        {
            ValidateIssuer = true,            // must come from the lab realm
            ValidateAudience = true,          // must be meant for lab-api
            ValidateIssuerSigningKey = true,  // signature must match Keycloak's public key
            ValidateLifetime = true,          // must not be expired
            ClockSkew = TimeSpan.Zero,        // default is 5 min of grace after expiry; we want none
            NameClaimType = "preferred_username",
            RoleClaimType = "role"
        };
        options.Events = new JwtBearerEvents
        {
            // Keycloak nests realm roles as "realm_access": { "roles": [ ... ] }, which .NET
            // doesn't understand. Copy each one into a flat "role" claim so RequireRole works.
            OnTokenValidated = context =>
            {
                var identity = (ClaimsIdentity)context.Principal!.Identity!;
                foreach (var role in RealmRoles(identity))
                    identity.AddClaim(new Claim("role", role));
                return Task.CompletedTask;
            }
        };
    });

// US-5: authorization — a valid token is not enough, it must also carry the admin role.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));

// US-7: one shared Redis connection. abortConnect=false lets the API start, and keep
// reconnecting in the background, even when Redis is down. FailFast makes commands fail
// immediately while disconnected, instead of queueing for 5s hoping Redis comes back.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var redisOptions = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis")!);
    redisOptions.BacklogPolicy = BacklogPolicy.FailFast;
    return ConnectionMultiplexer.Connect(redisOptions);
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// US-3: no authentication — proves the API itself is up.
app.MapGet("/api/public", () => new
{
    message = "Hello from the public endpoint. No token needed.",
    time = DateTimeOffset.UtcNow
});

// US-4: any valid token from the lab realm. Echoes back what the token carries.
app.MapGet("/api/protected", (ClaimsPrincipal user) => new
{
    message = "Your Keycloak token is valid.",
    username = user.Identity?.Name,
    roles = user.FindAll("role").Select(c => c.Value)
}).RequireAuthorization();

// US-5: 401 without a valid token, 403 with a valid token that lacks the admin role.
app.MapGet("/api/admin", (ClaimsPrincipal user) => new
{
    message = $"Welcome, {user.Identity?.Name}. You have the admin role."
}).RequireAuthorization("AdminOnly");

// US-6: deliberately slow, so caching has something to improve.
// US-7: cache-aside in Redis — try the cache, else do the slow load and store it for 60s.
// Redis is an optimisation, not a dependency: if it's down we just skip the cache.
app.MapGet("/api/products", async (IConnectionMultiplexer redis, ILogger<Program> logger) =>
{
    const string cacheKey = "products:all";
    var cache = redis.GetDatabase();

    try
    {
        var cachedJson = await cache.StringGetAsync(cacheKey);
        if (cachedJson.HasValue)
            return new ProductsResponse(Cached: true, JsonSerializer.Deserialize<Product[]>(cachedJson.ToString())!);
    }
    catch (Exception ex) when (ex is RedisException or TimeoutException)
    {
        logger.LogWarning("Redis unavailable, reading without cache: {Message}", ex.Message);
    }

    var products = await LoadProductsSlowly();

    try
    {
        await cache.StringSetAsync(cacheKey, JsonSerializer.Serialize(products), TimeSpan.FromSeconds(60));
    }
    catch (Exception ex) when (ex is RedisException or TimeoutException)
    {
        logger.LogWarning("Redis unavailable, not caching: {Message}", ex.Message);
    }

    return new ProductsResponse(Cached: false, products);
});

app.Run();

// Stands in for a slow database query.
static async Task<Product[]> LoadProductsSlowly()
{
    await Task.Delay(TimeSpan.FromSeconds(2));
    return
    [
        new(1, "Keyboard", 49.99m),
        new(2, "Mouse", 19.99m),
        new(3, "Monitor", 189.00m),
        new(4, "Headset", 59.50m)
    ];
}

static string[] RealmRoles(ClaimsIdentity identity)
{
    var realmAccess = identity.FindFirst("realm_access")?.Value;
    if (realmAccess is null) return [];

    using var json = JsonDocument.Parse(realmAccess);
    return json.RootElement.TryGetProperty("roles", out var roles)
        ? roles.EnumerateArray().Select(r => r.GetString()!).ToArray()
        : [];
}

record Product(int Id, string Name, decimal Price);

record ProductsResponse(bool Cached, Product[] Products);
