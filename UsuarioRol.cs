using System.Text.Json.Serialization;
using Nj = Newtonsoft.Json;

namespace VemoLab.Api;

// Un documento por persona autorizada. Vive en el contenedor "usuarios_roles".
// id = correo en minusculas (es tambien la llave de particion: busqueda directa por id).
//   roles: "admin", "aprobador" y/o "lector"
//   hubs:  hubs que puede ver/resolver. Vacio = depende del rol (ver UserAccess.Scope)
public class UsuarioRol
{
    [JsonPropertyName("id"), Nj.JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("nombre"), Nj.JsonProperty("nombre")] public string? Nombre { get; set; }
    [JsonPropertyName("roles"), Nj.JsonProperty("roles")] public List<string> Roles { get; set; } = new();
    [JsonPropertyName("hubs"), Nj.JsonProperty("hubs")] public List<string> Hubs { get; set; } = new();
    [JsonPropertyName("activo"), Nj.JsonProperty("activo")] public bool Activo { get; set; } = true;
}
