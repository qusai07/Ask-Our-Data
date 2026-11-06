using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace AskOurData.Gateway.Security;

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public string Mode { get; set; } = "Entra";   // Entra | Dev
    public DevAuthOptions Dev { get; set; } = new();
    public EntraAuthOptions Entra { get; set; } = new();
}

public sealed class DevAuthOptions
{
    public string Issuer { get; set; } = "askourdata-dev";
    public string Audience { get; set; } = "askourdata-gateway";
    public string SigningKey { get; set; } = "";
    public Dictionary<string, DevUser> Users { get; set; } = [];
}

/// <summary>Dev identity only. Roles/permissions/scope come from OpsDb sec.* (seeded), never from the token.</summary>
public sealed class DevUser
{
    public string Name { get; set; } = "";
}

public sealed class EntraAuthOptions
{
    public string Authority { get; set; } = "";   // https://login.microsoftonline.com/{tenant}/v2.0
    public string Audience { get; set; } = "";    // api://ask-our-data-gateway
}

public static class AuthSetup
{
    public static void AddGatewayAuth(this WebApplicationBuilder builder)
    {
        var auth = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        builder.Services.AddSingleton(auth);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;   // keep "oid" as-is; authorization comes from OpsDb, not token roles
            if (auth.Mode == "Dev")
            {
                if (!builder.Environment.IsDevelopment())
                    throw new InvalidOperationException("Dev auth mode is only allowed in the Development environment.");
                if (Encoding.UTF8.GetByteCount(auth.Dev.SigningKey) < 32)
                    throw new InvalidOperationException("Dev signing key must be at least 32 bytes.");
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = auth.Dev.Issuer,
                    ValidAudience = auth.Dev.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.Dev.SigningKey)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RoleClaimType = "roles",
                    NameClaimType = "name",
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            }
            else
            {
                o.Authority = auth.Entra.Authority;
                o.Audience = auth.Entra.Audience;
                o.TokenValidationParameters = new TokenValidationParameters { RoleClaimType = "roles", NameClaimType = "name" };
            }
        });
        builder.Services.AddAuthorization();
    }

    /// <summary>Dev-only token minting for the demo users configured in appsettings.Development.json.</summary>
    public static string MintDevToken(DevAuthOptions dev, string userKey, DevUser user, TimeSpan lifetime)
    {
        var claims = new List<Claim> { new("oid", "dev-" + userKey), new("name", user.Name), new("sub", "dev-" + userKey) };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = dev.Issuer,
            Audience = dev.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = DateTime.UtcNow,
            Expires = DateTime.UtcNow.Add(lifetime),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(dev.SigningKey)), SecurityAlgorithms.HmacSha256),
        });
    }
}
