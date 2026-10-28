using System.Security.Claims;

namespace VemoLab.Api;

// Roles de la lista usuarios_roles. Se usan en [Authorize(Roles = ...)]: la coma significa "cualquiera de".
public static class AppRoles
{
    public const string Admin = "admin";
    public const string Aprobador = "aprobador";
    public const string Lector = "lector";

    public const string Cualquiera = Admin + "," + Aprobador + "," + Lector;
    public const string Resolver = Admin + "," + Aprobador;

    public static readonly string[] Todos = { Admin, Aprobador, Lector };
}

// Que hubs puede ver una persona
public enum HubScope
{
    Todos,      // admin, o lector sin hubs asignados
    Asignados,  // tiene hubs asignados (aprobador o lector)
    Ninguno     // aprobador sin hubs, o sin rol
}

// Reglas de acceso calculadas a partir de los claims que arma EasyAuthHandler.
// Hubs en minusculas para comparar sin importar como se escribieron en Cosmos.
public static class UserAccess
{
    public const string EmailClaim = "email";
    public const string HubClaim = "hub";
    public const string EstadoClaim = "estado";   // "activo", "inactivo" o "sin_registro"

    public static string? Email(this ClaimsPrincipal user) => user.FindFirst(EmailClaim)?.Value;

    public static string Estado(this ClaimsPrincipal user) => user.FindFirst(EstadoClaim)?.Value ?? "sin_registro";

    public static List<string> Hubs(this ClaimsPrincipal user) =>
        user.FindAll(HubClaim).Select(c => c.Value).ToList();

    public static List<string> Roles(this ClaimsPrincipal user) =>
        user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(AppRoles.Admin);

    public static HubScope Scope(this ClaimsPrincipal user)
    {
        if (user.IsAdmin()) return HubScope.Todos;
        if (!AppRoles.Todos.Any(user.IsInRole)) return HubScope.Ninguno;
        if (user.Hubs().Count > 0) return HubScope.Asignados;
        if (user.IsInRole(AppRoles.Lector)) return HubScope.Todos;
        return HubScope.Ninguno;   // aprobador sin hubs: por seguridad no ve nada
    }

    public static bool CanSee(this ClaimsPrincipal user, string? hub) => user.Scope() switch
    {
        HubScope.Todos => true,
        HubScope.Asignados => hub != null && user.Hubs().Contains(hub.Trim().ToLowerInvariant()),
        _ => false
    };

    // Admin resuelve en cualquier hub; aprobador solo en sus hubs; lector nunca
    public static bool CanResolve(this ClaimsPrincipal user, string? hub)
    {
        if (user.IsAdmin()) return true;
        return user.IsInRole(AppRoles.Aprobador)
            && hub != null
            && user.Hubs().Contains(hub.Trim().ToLowerInvariant());
    }

    public static bool CanResolveAny(this ClaimsPrincipal user) =>
        user.IsAdmin() || (user.IsInRole(AppRoles.Aprobador) && user.Hubs().Count > 0);
}
