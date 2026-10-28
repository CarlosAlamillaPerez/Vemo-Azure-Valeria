using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Nj = Newtonsoft.Json;

namespace VemoLab.Api.Controllers;

// Una sola clase para ORDS (System.Text.Json), la API (System.Text.Json) y Cosmos (Newtonsoft).
// Por eso cada propiedad lleva los dos atributos, con el mismo nombre en snake_case.
public class Justification
{
    // Cosmos exige una propiedad "id": se usa el folio
    [Nj.JsonProperty("id"), JsonIgnore]
    public string Id => Folio;

    [JsonPropertyName("folio"), Nj.JsonProperty("folio")] public string Folio { get; set; } = string.Empty;
    [JsonPropertyName("type"), Nj.JsonProperty("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("status"), Nj.JsonProperty("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("driver_name"), Nj.JsonProperty("driver_name")] public string? DriverName { get; set; }
    [JsonPropertyName("plate"), Nj.JsonProperty("plate")] public string? Plate { get; set; }
    [JsonPropertyName("hub"), Nj.JsonProperty("hub")] public string? Hub { get; set; }   // llave de particion
    [JsonPropertyName("received_at"), Nj.JsonProperty("received_at")] public string? ReceivedAt { get; set; }
    [JsonPropertyName("reason"), Nj.JsonProperty("reason")] public string? Reason { get; set; }
    [JsonPropertyName("shift_start"), Nj.JsonProperty("shift_start")] public string? ShiftStart { get; set; }
    [JsonPropertyName("car_delivered"), Nj.JsonProperty("car_delivered")] public string? CarDelivered { get; set; }
    [JsonPropertyName("battery_pct"), Nj.JsonProperty("battery_pct")] public int? BatteryPct { get; set; }
    [JsonPropertyName("charging_minutes"), Nj.JsonProperty("charging_minutes")] public int? ChargingMinutes { get; set; }
    [JsonPropertyName("requested_minutes"), Nj.JsonProperty("requested_minutes")] public int? RequestedMinutes { get; set; }
    [JsonPropertyName("approved_minutes"), Nj.JsonProperty("approved_minutes")] public int? ApprovedMinutes { get; set; }
    [JsonPropertyName("resolved_by"), Nj.JsonProperty("resolved_by")] public string? ResolvedBy { get; set; }
    [JsonPropertyName("resolved_by_email"), Nj.JsonProperty("resolved_by_email")] public string? ResolvedByEmail { get; set; }
    [JsonPropertyName("resolved_at"), Nj.JsonProperty("resolved_at")] public string? ResolvedAt { get; set; }
    [JsonPropertyName("note"), Nj.JsonProperty("note")] public string? Note { get; set; }

    // Estado del envio a SharePoint: "sincronizado", "error" o vacio si todavia no aplica
    [JsonPropertyName("sharepoint_status"), Nj.JsonProperty("sharepoint_status")] public string? SharepointStatus { get; set; }
    [JsonPropertyName("sharepoint_synced_at"), Nj.JsonProperty("sharepoint_synced_at")] public string? SharepointSyncedAt { get; set; }
    [JsonPropertyName("sharepoint_error"), Nj.JsonProperty("sharepoint_error")] public string? SharepointError { get; set; }

    // Control de concurrencia de Cosmos: evita resolver dos veces la misma solicitud
    [JsonIgnore, Nj.JsonProperty("_etag", NullValueHandling = Nj.NullValueHandling.Ignore)]
    public string? ETag { get; set; }
}

public class OrdsResponse
{
    [JsonPropertyName("requests")] public List<Justification>? Requests { get; set; }
}

public class PartialInput
{
    public int Minutes { get; set; }
    public string? Note { get; set; }
}

public class RejectInput
{
    public string? Note { get; set; }
}

// Acceso (usuarios_roles, ver UserAccess):
//   ver la lista      -> admin, aprobador o lector, filtrado por hubs
//   resolver          -> admin (todos los hubs) o aprobador (solo sus hubs)
//   importar/reiniciar -> solo admin
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = AppRoles.Cualquiera)]
public class JustificacionesController : ControllerBase
{
    private const int MaxNoteLength = 300;

    private readonly CosmosStore _store;
    private readonly SharePointSync _sharePoint;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<JustificacionesController> _logger;

    public JustificacionesController(
        CosmosStore store, SharePointSync sharePoint, IHttpClientFactory http, IConfiguration config,
        ILogger<JustificacionesController> logger)
    {
        _store = store;
        _sharePoint = sharePoint;
        _http = http;
        _config = config;
        _logger = logger;
    }

    private static JsonResult Result(bool success, string message, object? data = null)
        => new(new { success, message, data });

    private static JsonResult Denied(string message)
        => new(new { success = false, message, data = (object?)null }) { StatusCode = StatusCodes.Status403Forbidden };

    private static string NowMexico()
    {
        DateTime now;
        try
        {
            now = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City"));
        }
        catch
        {
            now = DateTime.UtcNow.AddHours(-6);
        }
        return now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private JsonResult Fail(Exception ex)
    {
        _logger.LogError(ex, "Error en justificaciones");
        var msg = ex is InvalidOperationException
            ? ex.Message
            : "No se pudo completar la operacion. Intenta de nuevo en un momento.";
        return Result(false, msg);
    }

    // ORDS ahora solo sirve como fuente de datos de prueba para cargar en Cosmos
    private async Task<List<Justification>> FetchOrdsAsync(CancellationToken ct)
    {
        var url = _config["Valeria:OrdsUrl"];
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Falta la configuracion Valeria:OrdsUrl");

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        var response = await client.GetFromJsonAsync<OrdsResponse>(url, ct);
        if (response?.Requests == null)
            throw new InvalidOperationException("La respuesta de ORDS no trae la lista 'requests'");

        return response.Requests;
    }

    private static async Task<List<Justification>> QueryAsync(
        Container container, QueryDefinition query, CancellationToken ct)
    {
        var list = new List<Justification>();
        using var iterator = container.GetItemQueryIterator<Justification>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            list.AddRange(page);
        }
        return list;
    }

    private static async Task<Justification?> FindAsync(Container container, string folio, CancellationToken ct)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.id = @id")
            .WithParameter("@id", folio.Trim());
        var found = await QueryAsync(container, query, ct);
        return found.FirstOrDefault();
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        try
        {
            QueryDefinition query;
            switch (User.Scope())
            {
                case HubScope.Todos:
                    query = new QueryDefinition("SELECT * FROM c");
                    break;
                case HubScope.Asignados:
                    // Hubs del usuario ya vienen en minusculas; LOWER evita fallar por mayusculas en el documento
                    query = new QueryDefinition("SELECT * FROM c WHERE ARRAY_CONTAINS(@hubs, LOWER(c.hub))")
                        .WithParameter("@hubs", User.Hubs());
                    break;
                default:
                    return Result(true, "No tienes hubs asignados. Pide que te asignen uno.", new List<Justification>());
            }

            var container = await _store.GetContainerAsync(ct);
            var items = await QueryAsync(container, query, ct);
            items = items.OrderByDescending(x => x.ReceivedAt).ToList();
            return Result(true, "OK", items);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    // Carga las solicitudes de ORDS que todavia no existan en Cosmos
    [HttpPost("importar")]
    [Authorize(Roles = AppRoles.Admin)]
    public Task<IActionResult> Import(CancellationToken ct) => LoadTestDataAsync(false, ct);

    // Vuelve a cargar todas las de ORDS, pisando las decisiones tomadas
    [HttpPost("reiniciar")]
    [Authorize(Roles = AppRoles.Admin)]
    public Task<IActionResult> Reset(CancellationToken ct) => LoadTestDataAsync(true, ct);

    private async Task<IActionResult> LoadTestDataAsync(bool overwrite, CancellationToken ct)
    {
        try
        {
            var source = await FetchOrdsAsync(ct);
            var container = await _store.GetContainerAsync(ct);
            int loaded = 0, skipped = 0;

            foreach (var item in source)
            {
                if (overwrite)
                {
                    await container.UpsertItemAsync(item, new PartitionKey(item.Hub), cancellationToken: ct);
                    loaded++;
                }
                else
                {
                    try
                    {
                        await container.CreateItemAsync(item, new PartitionKey(item.Hub), cancellationToken: ct);
                        loaded++;
                    }
                    catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                    {
                        skipped++;
                    }
                }
            }

            var message = overwrite
                ? $"Se reiniciaron {loaded} solicitudes de prueba"
                : $"Se cargaron {loaded} solicitudes nuevas ({skipped} ya existian)";
            return Result(true, message);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    // Resolucion comun: busca la solicitud, confirma que siga pendiente, aplica el cambio y la guarda
    private async Task<IActionResult> ResolveAsync(
        string folio, Func<Justification, string?> apply, string okMessage, CancellationToken ct)
    {
        var container = await _store.GetContainerAsync(ct);
        var item = await FindAsync(container, folio, ct);
        // Fuera de su alcance se responde igual que si no existiera: no se revela que hay en otros hubs
        if (item == null || !User.CanSee(item.Hub)) return Result(false, "No se encontro la solicitud");
        if (!User.CanResolve(item.Hub)) return Denied($"No puedes resolver solicitudes del hub {item.Hub}");
        if (item.Status != "pendiente") return Result(false, "La solicitud ya fue resuelta");

        var error = apply(item);
        if (error != null) return Result(false, error);

        item.ResolvedBy = User.Identity?.Name;   // nombre de usuarios_roles (o el correo si no tiene)
        item.ResolvedByEmail = User.Email();
        item.ResolvedAt = NowMexico();

        ItemResponse<Justification> saved;
        try
        {
            saved = await container.ReplaceItemAsync(
                item, item.Id, new PartitionKey(item.Hub),
                new ItemRequestOptions { IfMatchEtag = item.ETag }, ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return Result(false, "La solicitud ya fue resuelta");
        }

        // La decision ya quedo en Cosmos. Desde aqui, un fallo de SharePoint no la revierte:
        // se marca en el documento para poder reenviarla despues.
        var resolved = saved.Resource;
        resolved.ETag = saved.ETag;

        var (sent, sendError) = await _sharePoint.SendAsync(resolved, ct);
        resolved.SharepointStatus = sent ? "sincronizado" : "error";
        resolved.SharepointSyncedAt = sent ? NowMexico() : null;
        resolved.SharepointError = sent ? null : sendError;

        try
        {
            await container.ReplaceItemAsync(
                resolved, resolved.Id, new PartitionKey(resolved.Hub),
                new ItemRequestOptions { IfMatchEtag = resolved.ETag }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar el estado de SharePoint de {Folio}", resolved.Folio);
        }

        return Result(true, sent ? okMessage : okMessage + " (no se pudo enviar a SharePoint)");
    }

    [HttpPost("{folio}/aprobar")]
    [Authorize(Roles = AppRoles.Resolver)]
    public async Task<IActionResult> Approve(string folio, CancellationToken ct)
    {
        try
        {
            return await ResolveAsync(folio, item =>
            {
                item.Status = "aprobada";
                item.ApprovedMinutes = item.RequestedMinutes ?? 0;
                return null;
            }, "Justificacion aprobada", ct);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [HttpPost("{folio}/rechazar")]
    [Authorize(Roles = AppRoles.Resolver)]
    public async Task<IActionResult> Reject(string folio, [FromBody] RejectInput? input, CancellationToken ct)
    {
        try
        {
            var note = input?.Note?.Trim();
            if (note != null && note.Length > MaxNoteLength)
                return Result(false, $"El motivo no puede pasar de {MaxNoteLength} caracteres");

            return await ResolveAsync(folio, item =>
            {
                item.Status = "rechazada";
                item.ApprovedMinutes = 0;
                item.Note = string.IsNullOrWhiteSpace(note) ? null : note;
                return null;
            }, "Justificacion rechazada", ct);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [HttpPost("{folio}/parcial")]
    [Authorize(Roles = AppRoles.Resolver)]
    public async Task<IActionResult> ApprovePartial(string folio, [FromBody] PartialInput? input, CancellationToken ct)
    {
        try
        {
            if (input == null) return Result(false, "Faltan los datos de la aprobacion parcial");

            var note = input.Note?.Trim();
            if (string.IsNullOrWhiteSpace(note))
                return Result(false, "La nota es obligatoria en una aprobacion parcial");
            if (note.Length > MaxNoteLength)
                return Result(false, $"La nota no puede pasar de {MaxNoteLength} caracteres");

            return await ResolveAsync(folio, item =>
            {
                var requested = item.RequestedMinutes ?? 0;
                if (input.Minutes <= 0 || input.Minutes >= requested)
                    return $"Los minutos aprobados deben ser mayores a 0 y menores a {requested}";

                item.Status = "parcial";
                item.ApprovedMinutes = input.Minutes;
                item.Note = note;
                return null;
            }, "Justificacion aprobada parcialmente", ct);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }
}
