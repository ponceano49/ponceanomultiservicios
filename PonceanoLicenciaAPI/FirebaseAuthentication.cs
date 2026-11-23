using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace PonceanoLicenciaAPI;

public static class FirebaseAuthentication
{
    public const string ProjectIdPredeterminado = "ponceano-muktiservicios";

    public static IServiceCollection AddFirebaseIdTokenAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var projectId = configuration["Firebase:ProjectId"] ?? ProjectIdPredeterminado;
        if (string.IsNullOrWhiteSpace(projectId) || projectId.Contains('/') || projectId.Contains(':'))
            throw new InvalidOperationException("Configure un Firebase:ProjectId válido.");
        var issuer = $"https://securetoken.google.com/{projectId}";
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = issuer;
            options.Audience = projectId;
            options.RequireHttpsMetadata = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = projectId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2),
                NameClaimType = "email"
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var subject = context.Principal?.FindFirstValue("sub");
                    var email = context.Principal?.FindFirstValue("email");
                    var emailVerified = context.Principal?.FindFirstValue("email_verified");
                    if (string.IsNullOrWhiteSpace(subject) || !Correo.TryNormalizar(email, out _) ||
                        !string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase))
                        context.Fail("Se requiere un token Firebase válido con correo verificado.");
                    return Task.CompletedTask;
                }
            };
        });
        services.AddAuthorization();
        return services;
    }

    public static bool TryGetVerifiedEmail(ClaimsPrincipal principal, out string email)
    {
        email = string.Empty;
        return string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase) &&
            Correo.TryNormalizar(principal.FindFirstValue("email"), out email);
    }
}
