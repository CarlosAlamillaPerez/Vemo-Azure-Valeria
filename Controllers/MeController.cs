using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace VemoLab.Api.Controllers;

// Quien soy y que puedo hacer. La pagina lo llama al cargar para decidir que mostrar.
// Solo exige sesion (no rol): asi una persona sin alta recibe "sin acceso" en vez de un error.
[ApiController]
[Route("api/me")]
[Authorize]
public class MeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var roles = User.Roles();
        var data = new
        {
            email = User.Email(),
            nombre = User.Identity?.Name,
            estado = User.Estado(),                              // activo, inactivo, sin_registro
            tiene_acceso = roles.Count > 0,
            roles,
            hubs = User.Hubs(),
            alcance = User.Scope().ToString().ToLowerInvariant(), // todos, asignados, ninguno
            es_admin = User.IsAdmin(),
            puede_resolver = User.CanResolveAny()
        };
        return new JsonResult(new { success = true, message = "OK", data });
    }
}
