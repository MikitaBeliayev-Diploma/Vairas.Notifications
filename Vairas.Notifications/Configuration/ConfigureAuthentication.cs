using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Vairas.Notifications.Configuration.Options;
using Vairas.Notifications.Middleware;

namespace Vairas.Notifications.Configuration;

/// <summary>Bearer authentication for user tokens issued by the Authentication service.</summary>
public static class ConfigureAuthentication
{
    public static IServiceCollection AddBearerAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IOptions<JwtOptions>>((bearer, jwt) =>
        {
            var settings = jwt.Value;
            bearer.Authority = settings.Authority;
            bearer.MapInboundClaims = false;
            bearer.RequireHttpsMetadata = true;
            bearer.TokenValidationParameters = new()
            {
                ValidateIssuer = true, ValidIssuer = settings.Issuer,
                ValidateAudience = true, ValidAudience = settings.UserAudience,
                ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], NameClaimType = "sub"
            };
            bearer.Events = new()
            {
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    await context.HttpContext.WriteProblemAsync(401, "AUTHENTICATION_REQUIRED", "Authentication required.");
                },
                OnForbidden = context => context.HttpContext.WriteProblemAsync(403, "ACCESS_DENIED", "Access denied.")
            };
        });
        return services;
    }
}
