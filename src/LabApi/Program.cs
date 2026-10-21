using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;

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
            NameClaimType = "preferred_username"
        };
    });
builder.Services.AddAuthorization();

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
    roles = RealmRoles(user)
}).RequireAuthorization();

app.Run();

// Keycloak puts realm roles in a nested claim: "realm_access": { "roles": [ ... ] }
static string[] RealmRoles(ClaimsPrincipal user)
{
    var realmAccess = user.FindFirst("realm_access")?.Value;
    if (realmAccess is null) return [];

    using var json = JsonDocument.Parse(realmAccess);
    return json.RootElement.TryGetProperty("roles", out var roles)
        ? roles.EnumerateArray().Select(r => r.GetString()!).ToArray()
        : [];
}
