using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace VemoLab.Api;

// Convierte la sesion de Easy Auth en un usuario de ASP.NET Core con los roles y hubs de usuarios_roles.
//
// QUIEN ERES: en Azure, Easy Auth valida el login de Entra y agrega el encabezado X-MS-CLIENT-PRINCIPAL
//             (Base64 con los claims). Easy Auth lo borra si viene del navegador, por eso es confiable,
//             PERO solo si Easy Auth esta activo: si no, cualquiera podria mandarlo. Por eso se exige
//             WEBSITE_AUTH_ENABLED=true (lo pone App Service) o Auth:EasyAuthEnabled=true.
//             En Development no hay Easy Auth: se usa el correo de Auth:DevUser (user secrets).
// QUE PUEDES: se busca el correo en usuarios_roles. Sin registro o inactivo = autenticado sin roles.
public class EasyAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "EasyAuth";
    private const string ErrorItemKey = "EasyAuthError";

    // Orden de preferencia para encontrar el correo corporativo entre los claims de Entra
    private static readonly string[] EmailClaimTypes =
    {
        "preferred_username", "email", ClaimTypes.Email, "upn", ClaimTypes.Upn
    };

    private readonly CosmosStore _store;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public EasyAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        CosmosStore store, IConfiguration config, IWebHostEnvironment env)
        : base(options, logger, encoder)
    {
        _store = store;
        _config = config;
        _env = env;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var email = ReadEmail();
        if (email == null) return AuthenticateResult.NoResult();

        UsuarioRol? usuario;
        try
        {
            usuario = await _store.GetUsuarioAsync(email, Context.RequestAborted);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "No se pudo consultar usuarios_roles");
            Context.Items[ErrorItemKey] = "No se pudo validar tu acceso. Intenta de nuevo en un momento.";
            return AuthenticateResult.Fail("No se pudo consultar usuarios_roles");
        }

        var estado = usuario == null ? "sin_registro" : usuario.Activo ? "activo" : "inactivo";

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, usuario?.Nombre is { } nombre && !string.IsNullOrWhiteSpace(nombre) ? nombre.Trim() : email),
            new(UserAccess.EmailClaim, email),
            new(UserAccess.EstadoClaim, estado)
        };

        // Solo un usuario activo recibe roles y hubs; los demas quedan autenticados pero sin permisos
        if (usuario is { Activo: true })
        {
            foreach (var role in (usuario.Roles ?? new()).Select(r => r.Trim().ToLowerInvariant()).Distinct())
                if (AppRoles.Todos.Contains(role)) claims.Add(new Claim(ClaimTypes.Role, role));

            foreach (var hub in (usuario.Hubs ?? new()).Where(h => !string.IsNullOrWhiteSpace(h))
                         .Select(h => h.Trim().ToLowerInvariant()).Distinct())
                claims.Add(new Claim(UserAccess.HubClaim, hub));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private string? ReadEmail()
    {
        if (_env.IsDevelopment())
        {
            var devUser = _config["Auth:DevUser"];
            return string.IsNullOrWhiteSpace(devUser) ? null : CosmosStore.NormalizeEmail(devUser);
        }

        var easyAuthOn =
            string.Equals(Environment.GetEnvironmentVariable("WEBSITE_AUTH_ENABLED"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_config["Auth:EasyAuthEnabled"], "true", StringComparison.OrdinalIgnoreCase);
        if (!easyAuthOn)
        {
            Logger.LogWarning("Easy Auth no esta activo: se ignoran los encabezados X-MS-CLIENT-PRINCIPAL");
            return null;
        }

        var header = Request.Headers["X-MS-CLIENT-PRINCIPAL"].ToString();
        if (!string.IsNullOrEmpty(header))
        {
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(header)));
                if (doc.RootElement.TryGetProperty("claims", out var claims) && claims.ValueKind == JsonValueKind.Array)
                {
                    foreach (var type in EmailClaimTypes)
                    {
                        foreach (var claim in claims.EnumerateArray())
                        {
                            if (claim.TryGetProperty("typ", out var typ) && typ.GetString() == type
                                && claim.TryGetProperty("val", out var val)
                                && val.GetString() is { } value && value.Contains('@'))
                            {
                                return CosmosStore.NormalizeEmail(value);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                Logger.LogWarning("X-MS-CLIENT-PRINCIPAL con formato inesperado: {Tipo}", ex.GetType().Name);
            }
        }

        // Respaldo: Easy Auth tambien manda el nombre de usuario en este encabezado
        var name = Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].ToString();
        return name.Contains('@') ? CosmosStore.NormalizeEmail(name) : null;
    }

    // Sin sesion: 401 con el contrato { success, message, data } para que la pagina lo muestre
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteJsonAsync(StatusCodes.Status401Unauthorized,
            Context.Items[ErrorItemKey] as string
            ?? (_env.IsDevelopment()
                ? "No hay usuario de prueba: agrega Auth:DevUser en user secrets."
                : "No se identifico tu sesion. Recarga la pagina para iniciar sesion."));

    // Con sesion pero sin el rol necesario: 403
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteJsonAsync(StatusCodes.Status403Forbidden, "No tienes permiso para esta accion.");

    private Task WriteJsonAsync(int status, string message)
    {
        Response.StatusCode = status;
        return Response.WriteAsJsonAsync(new { success = false, message, data = (object?)null });
    }
}
