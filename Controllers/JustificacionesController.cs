using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;

namespace VemoLab.Api.Controllers;

public class PartialInput
{
    public int Minutes { get; set; }
    public string? Note { get; set; }
}

public class RejectInput
{
    public string? Note { get; set; }
}

public class SimularInput
{
    public int? Cantidad { get; set; }
}

// El endpoint ORDS de prueba (IA_VALERIA) devuelve las solicitudes con la misma forma que manda Valeria IA
public class OrdsResponse
{
    [JsonPropertyName("requests")] public List<SolicitudInput>? Requests { get; set; }
}

// Flujo: Valeria IA -> POST /api/justificaciones (guarda en Cosmos, estatus "pendiente")
//        persona del hub -> aprobar / parcial / rechazar -> Cosmos + SharePoint + ticket (Storage) + aviso a Valeria
//        "Ver ticket" -> GET /api/justificaciones/{folio}/ticket (enlace temporal)
//
// Acceso (usuarios_roles, ver UserAccess):
//   recibir solicitudes -> abierto por ahora (lo llama Valeria IA; su autenticacion se define despues)
//   ver la lista        -> admin, aprobador o lector, filtrado por hubs
//   resolver            -> admin (todos los hubs) o aprobador (solo sus hubs)
//   cargar pruebas (ORDS) / simular / borrar pruebas -> solo admin
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = AppRoles.Cualquiera)]
public class JustificacionesController : ControllerBase
{
    private const int MaxNoteLength = 300;
    private const int MaxMinutosSolicitados = 24 * 60;
    private const string PrefijoSimulada = Pruebas.PrefijoSimulada;   // creadas con "Simular solicitud"
    private const string PrefijoOrds = Pruebas.PrefijoOrds;           // cargadas desde el ORDS de prueba
    private static readonly TimeSpan VigenciaEnlaceVista = TimeSpan.FromMinutes(10);   // enlace para el boton "Ver ticket"

    private readonly CosmosStore _store;
    private readonly SharePointSync _sharePoint;
    private readonly TicketStorage _tickets;
    private readonly ValeriaNotifier _valeria;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<JustificacionesController> _logger;

    public JustificacionesController(
        CosmosStore store, SharePointSync sharePoint, TicketStorage tickets, ValeriaNotifier valeria,
        IHttpClientFactory http, IConfiguration config, ILogger<JustificacionesController> logger)
    {
        _store = store;
        _sharePoint = sharePoint;
        _tickets = tickets;
        _valeria = valeria;
        _http = http;
        _config = config;
        _logger = logger;
    }

    private static JsonResult Result(bool success, string message, object? data = null)
        => new(new { success, message, data });

    private static JsonResult ErrorResult(int status, string message)
        => new(new { success = false, message, data = (object?)null }) { StatusCode = status };

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

    private static async Task<List<Solicitud>> QueryAsync(
        Container container, QueryDefinition query, CancellationToken ct)
    {
        var list = new List<Solicitud>();
        using var iterator = container.GetItemQueryIterator<Solicitud>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            list.AddRange(page);
        }
        return list;
    }

    private static async Task<Solicitud?> FindAsync(Container container, string folio, CancellationToken ct)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.tipo_doc = \"solicitud\" AND c.id = @id")
            .WithParameter("@id", folio.Trim());
        var found = await QueryAsync(container, query, ct);
        return found.FirstOrDefault();
    }

    // ---------------------------------------------------------------- listar

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        try
        {
            QueryDefinition query;
            switch (User.Scope())
            {
                case HubScope.Todos:
                    query = new QueryDefinition("SELECT * FROM c WHERE c.tipo_doc = \"solicitud\"");
                    break;
                case HubScope.Asignados:
                    // Hubs del usuario ya vienen en minusculas; LOWER evita fallar por mayusculas en el documento
                    query = new QueryDefinition(
                            "SELECT * FROM c WHERE c.tipo_doc = \"solicitud\" AND ARRAY_CONTAINS(@hubs, LOWER(c.hub))")
                        .WithParameter("@hubs", User.Hubs());
                    break;
                default:
                    return Result(true, "No tienes hubs asignados. Pide que te asignen uno.", new List<Solicitud>());
            }

            var container = await _store.GetContainerAsync(ct);
            var items = await QueryAsync(container, query, ct);
            items = items.OrderByDescending(x => x.RecibidoEl).ThenByDescending(x => x.Folio).ToList();
            return Result(true, "OK", items);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    // ---------------------------------------------------------------- recibir (Valeria IA)

    private sealed record CreateOutcome(Solicitud? Item, bool Duplicada, string? Error, string? Aviso);

    // Valida lo que manda Valeria IA, evita duplicados por conversation_id y guarda en Cosmos.
    // Lo usan tanto el endpoint real como el simulador, para que las pruebas recorran el mismo camino.
    private async Task<CreateOutcome> CreateAsync(SolicitudInput? input, CancellationToken ct)
    {
        if (input == null) return new(null, false, "Faltan los datos de la solicitud", null);

        var errors = new List<string>();

        string Required(string? value, string field, int max)
        {
            var v = value?.Trim();
            if (string.IsNullOrEmpty(v)) errors.Add($"{field} es obligatorio");
            else if (v.Length > max) errors.Add($"{field} no puede pasar de {max} caracteres");
            return v ?? string.Empty;
        }

        string? Optional(string? value, string field, int max)
        {
            var v = value?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            if (v.Length > max) errors.Add($"{field} no puede pasar de {max} caracteres");
            return v;
        }

        var categoria = Required(input.Categoria, "categoria", 120);
        var nombre = Required(input.NombreCompleto, "nombre_completo", 150);
        var telefono = Required(input.NumeroTelefono, "numero_telefono", 30);
        var conversationId = Required(input.ConversationId, "conversation_id", 120);
        var hub = Required(input.Hub, "hub", 60);
        var placas = Required(input.Placas, "placas", 20);
        var horaInicio = Optional(input.Horario?.HoraInicio, "horario.hora_inicio", 20);
        var horaFin = Optional(input.Horario?.HoraFin, "horario.hora_fin", 20);
        var motivo = Optional(input.MotivoDesconexion, "motivo_desconexion", 1000);

        var horas = 0;
        var minutos = 0;
        long total = 0;
        if (input.TiempoDesconexion == null)
        {
            errors.Add("tiempo_desconexion es obligatorio");
        }
        else
        {
            horas = input.TiempoDesconexion.Horas;
            minutos = input.TiempoDesconexion.Minutos;
            total = (long)horas * 60 + minutos;
            if (horas < 0 || minutos < 0) errors.Add("tiempo_desconexion no puede ser negativo");
            else if (total <= 0) errors.Add("tiempo_desconexion debe ser mayor a cero");
            else if (total > MaxMinutosSolicitados) errors.Add("tiempo_desconexion no puede pasar de 24 horas");
        }

        if (errors.Count > 0) return new(null, false, string.Join("; ", errors), null);

        var container = await _store.GetContainerAsync(ct);

        // conversation_id es la clave unica: si Valeria IA reintenta, se devuelve la misma solicitud
        var existing = await QueryAsync(container,
            new QueryDefinition("SELECT * FROM c WHERE c.tipo_doc = \"solicitud\" AND c.conversation_id = @c")
                .WithParameter("@c", conversationId), ct);
        if (existing.Count > 0) return new(existing[0], true, null, null);

        var item = new Solicitud
        {
            Folio = await _store.NextFolioAsync(ct),
            Estatus = EstatusSolicitud.Pendiente,
            Categoria = categoria,
            NombreCompleto = nombre,
            NumeroTelefono = telefono,
            ConversationId = conversationId,
            Hub = hub,
            Placas = placas,
            HoraInicio = horaInicio,
            HoraFin = horaFin,
            TiempoHoras = horas,
            TiempoMinutos = minutos,
            MinutosSolicitados = (int)total,
            MotivoDesconexion = motivo,
            RecibidoEl = NowMexico()
        };

        await container.CreateItemAsync(item, new PartitionKey(item.Hub), cancellationToken: ct);

        string? aviso = null;
        if (!Categorias.EsConocida(categoria))
        {
            _logger.LogWarning("Solicitud {Folio} con categoria fuera del catalogo", item.Folio);
            aviso = "la categoria no coincide con el catalogo de SharePoint";
        }

        return new(item, false, null, aviso);
    }

    // Lo llama Valeria IA. Sin autenticacion por ahora (decision de Carlos): se agrega mas adelante.
    // Nota: Easy Auth sigue bloqueando a quien no tenga sesion hasta que se excluya esta ruta.
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Receive([FromBody] SolicitudInput? input, CancellationToken ct)
    {
        try
        {
            var outcome = await CreateAsync(input, ct);
            if (outcome.Error != null) return ErrorResult(StatusCodes.Status400BadRequest, outcome.Error);

            var item = outcome.Item!;
            var message = outcome.Duplicada
                ? "La solicitud ya habia sido recibida"
                : "Solicitud recibida" + (outcome.Aviso != null ? $" (aviso: {outcome.Aviso})" : "");

            return Result(true, message, new
            {
                folio = item.Folio,
                conversation_id = item.ConversationId,
                estatus = item.Estatus,
                duplicada = outcome.Duplicada
            });
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    // ---------------------------------------------------------------- cargar desde ORDS (pruebas locales)

    // Mientras Ale no nos pase su endpoint, el ORDS de prueba hace de "Valeria IA": cada elemento de
    // "requests" es el cuerpo de una solicitud. Se leen por el mismo camino que POST /api/justificaciones.
    private async Task<List<SolicitudInput>> FetchOrdsAsync(CancellationToken ct)
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

    // Trae las solicitudes del ORDS que todavia no existan. Como el ORDS siempre devuelve los mismos
    // conversation_id, las ya cargadas se cuentan como "ya existian" (es la prueba de duplicados).
    [HttpPost("importar")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Import(CancellationToken ct)
    {
        try
        {
            var source = await FetchOrdsAsync(ct);
            int loaded = 0, skipped = 0;
            var errors = new List<string>();

            foreach (var input in source)
            {
                // El prefijo permite borrar despues solo las de prueba, sin tocar las que mande Valeria IA
                if (!string.IsNullOrWhiteSpace(input.ConversationId))
                    input.ConversationId = PrefijoOrds + input.ConversationId.Trim();

                var outcome = await CreateAsync(input, ct);
                if (outcome.Error != null) errors.Add(outcome.Error);
                else if (outcome.Duplicada) skipped++;
                else loaded++;
            }

            var message = $"Se cargaron {loaded} solicitudes nuevas ({skipped} ya existian)";
            if (errors.Count > 0) message += $"; {errors.Count} con errores: {errors[0]}";
            return Result(loaded > 0 || errors.Count == 0, message);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    // ---------------------------------------------------------------- simulador (pruebas sin Ale)

    private static readonly string[] SimNombres =
    {
        "Juan Perez Morales", "Maria Lopez Cruz", "Carlos Ramos Vega", "Ana Torres Rivas",
        "Luis Ortega Paz", "Sofia Herrera Diaz", "Diego Castillo Nava", "Laura Mendez Rios"
    };

    private static readonly string[] SimHubs = { "Polanco", "Petrarca", "Iztapalapa" };

    private static readonly string[] SimCategorias =
    {
        "Justificar tiempo por entrega de unidad tardia por falta de carga",
        "Justificar tiempo por falla temporal en la unidad",
        "Justificar tiempo por entrega tardia de unidad por parte del HUB",
        "Justificar tiempo por taller",
        "Justificar tiempos por pinchadura"
    };

    private static readonly string[] SimMotivos =
    {
        "El auto llego tarde del turno anterior",
        "Auto entregado con bateria baja",
        "Entrega retrasada por revision del auto",
        "La unidad presento una falla y tuve que esperar",
        "Me entregaron el auto muy tarde"
    };

    private static readonly (int Horas, int Minutos)[] SimTiempos =
        { (0, 30), (1, 0), (1, 30), (2, 0), (2, 30) };

    private static SolicitudInput BuildSimulated()
    {
        var rnd = Random.Shared;
        var inicioHora = rnd.Next(5, 10);
        var duracion = rnd.Next(8, 11);
        var tiempo = SimTiempos[rnd.Next(SimTiempos.Length)];

        return new SolicitudInput
        {
            Categoria = SimCategorias[rnd.Next(SimCategorias.Length)],
            NombreCompleto = SimNombres[rnd.Next(SimNombres.Length)],
            NumeroTelefono = "55" + rnd.Next(10000000, 99999999),
            ConversationId = PrefijoSimulada + Guid.NewGuid().ToString("N")[..12],
            Hub = SimHubs[rnd.Next(SimHubs.Length)],
            Horario = new HorarioInput { HoraInicio = $"{inicioHora:00}:00", HoraFin = $"{inicioHora + duracion:00}:00" },
            Placas = $"MX{(char)rnd.Next('A', 'Z' + 1)}-{rnd.Next(100, 999)}-{(char)rnd.Next('A', 'Z' + 1)}",
            TiempoDesconexion = new TiempoInput { Horas = tiempo.Horas, Minutos = tiempo.Minutos },
            MotivoDesconexion = SimMotivos[rnd.Next(SimMotivos.Length)]
        };
    }

    // Crea solicitudes de prueba por el mismo camino que usa Valeria IA
    [HttpPost("simular")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Simulate([FromBody] SimularInput? input, CancellationToken ct)
    {
        try
        {
            var count = Math.Clamp(input?.Cantidad ?? 1, 1, 10);
            var folios = new List<long>();
            for (var i = 0; i < count; i++)
            {
                var outcome = await CreateAsync(BuildSimulated(), ct);
                if (outcome.Error != null) return Result(false, outcome.Error);
                folios.Add(outcome.Item!.Folio);
            }

            var message = count == 1
                ? $"Se simulo la solicitud {folios[0]}"
                : $"Se simularon {count} solicitudes ({string.Join(", ", folios)})";
            return Result(true, message, folios);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    // Borra solo las de prueba (conversation_id con prefijo "sim-" u "ords-"); las de Valeria IA no se tocan
    [HttpDelete("pruebas")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> DeleteSimulated(CancellationToken ct)
    {
        try
        {
            var container = await _store.GetContainerAsync(ct);
            var simuladas = await QueryAsync(container,
                new QueryDefinition(
                        "SELECT * FROM c WHERE c.tipo_doc = \"solicitud\" AND " +
                        "(STARTSWITH(c.conversation_id, @sim) OR STARTSWITH(c.conversation_id, @ords))")
                    .WithParameter("@sim", PrefijoSimulada)
                    .WithParameter("@ords", PrefijoOrds), ct);

            foreach (var item in simuladas)
            {
                await container.DeleteItemAsync<Solicitud>(item.Id, new PartitionKey(item.Hub), cancellationToken: ct);

                // El ticket de una prueba ya no sirve; si no se puede borrar, no estorba
                if (item.TicketBlob != null)
                {
                    try { await _tickets.DeleteAsync(item.Folio, ct); }
                    catch (Exception ex) { _logger.LogWarning("No se pudo borrar el ticket de {Folio}: {Tipo}", item.Folio, ex.GetType().Name); }
                }
            }

            return Result(true, $"Se borraron {simuladas.Count} solicitudes de prueba");
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    // ---------------------------------------------------------------- resolver

    // Resolucion comun: busca la solicitud, confirma que siga pendiente, aplica el cambio y la guarda
    private async Task<IActionResult> ResolveAsync(
        string folio, Func<Solicitud, string?> apply, string okMessage, CancellationToken ct)
    {
        var container = await _store.GetContainerAsync(ct);
        var item = await FindAsync(container, folio, ct);
        // Fuera de su alcance se responde igual que si no existiera: no se revela que hay en otros hubs
        if (item == null || !User.CanSee(item.Hub)) return Result(false, "No se encontro la solicitud");
        if (!User.CanResolve(item.Hub)) return ErrorResult(StatusCodes.Status403Forbidden, $"No puedes resolver solicitudes del hub {item.Hub}");
        if (item.Estatus != EstatusSolicitud.Pendiente) return Result(false, "La solicitud ya fue resuelta");

        var error = apply(item);
        if (error != null) return Result(false, error);

        item.ResueltoPor = User.Identity?.Name;   // nombre de usuarios_roles (o el correo si no tiene)
        item.ResueltoPorCorreo = User.Email();
        item.ResueltoEl = NowMexico();

        ItemResponse<Solicitud> saved;
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
        resolved.SharepointEstatus = sent ? "sincronizado" : "error";
        resolved.SharepointSincronizadoEl = sent ? NowMexico() : null;
        resolved.SharepointError = sent ? null : sendError;

        // Lo mismo con el ticket y el aviso a Valeria: si fallan, la decision se queda y se marca el error
        await GenerateTicketAsync(resolved, ct);
        await NotifyValeriaAsync(resolved, ct);

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

        var avisos = new List<string>();
        if (!sent) avisos.Add("no se pudo enviar a SharePoint");
        if (resolved.TicketBlob == null) avisos.Add("no se pudo generar el ticket");
        if (resolved.ValeriaEstatus == ValeriaNotifier.Error) avisos.Add("no se pudo avisar a Valeria");

        return Result(true, avisos.Count == 0 ? okMessage : $"{okMessage} ({string.Join("; ", avisos)})");
    }

    // ---------------------------------------------------------------- ticket y aviso a Valeria

    // Dibuja el ticket, lo sube al Storage y anota el resultado en el documento (no lo guarda: lo hace quien llama)
    private async Task GenerateTicketAsync(Solicitud item, CancellationToken ct)
    {
        try
        {
            var png = TicketImage.Render(item);
            await _tickets.UploadAsync(item.Folio, png, ct);
            item.TicketBlob = TicketStorage.BlobName(item.Folio);
            item.TicketGeneradoEl = NowMexico();
            item.TicketError = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo generar el ticket de {Folio}", item.Folio);
            item.TicketBlob = null;
            item.TicketError = ex is InvalidOperationException
                ? ex.Message
                : $"No se pudo generar el ticket ({ex.GetType().Name})";
        }
    }

    // Manda a Valeria IA el enlace firmado (vence en Storage:DiasVigenciaEnlace dias, por defecto 3)
    private async Task NotifyValeriaAsync(Solicitud item, CancellationToken ct)
    {
        var skip = _valeria.SkipReason(item);
        if (skip != null)
        {
            item.ValeriaEstatus = skip;
            item.ValeriaNotificadoEl = null;
            item.ValeriaError = null;
            return;
        }

        string? imgUrl = null;
        if (item.TicketBlob != null)
        {
            try
            {
                var uri = await _tickets.GetReadUriAsync(item.Folio, TimeSpan.FromDays(_tickets.DiasVigenciaEnlace), ct);
                imgUrl = uri?.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError("No se pudo firmar el enlace del ticket de {Folio}: {Tipo}", item.Folio, ex.GetType().Name);
            }
        }

        var (ok, error) = await _valeria.SendAsync(item, imgUrl, ct);
        item.ValeriaEstatus = ok ? ValeriaNotifier.Notificado : ValeriaNotifier.Error;
        item.ValeriaNotificadoEl = ok ? NowMexico() : null;
        item.ValeriaError = ok ? null : error;
    }

    // Enlace temporal para el boton "Ver ticket" de la pagina. Respeta los hubs de quien lo pide.
    [HttpGet("{folio}/ticket")]
    public async Task<IActionResult> GetTicket(string folio, CancellationToken ct)
    {
        try
        {
            var container = await _store.GetContainerAsync(ct);
            var item = await FindAsync(container, folio, ct);
            if (item == null || !User.CanSee(item.Hub)) return Result(false, "No se encontro la solicitud");
            if (item.Estatus == EstatusSolicitud.Pendiente)
                return Result(false, "Esta solicitud aun no tiene ticket: primero hay que resolverla");

            var uri = item.TicketBlob != null
                ? await _tickets.GetReadUriAsync(item.Folio, VigenciaEnlaceVista, ct)
                : null;

            if (uri == null)
            {
                // Resoluciones anteriores a los tickets, o un archivo que ya no esta: se genera ahora
                await GenerateTicketAsync(item, ct);
                if (item.TicketBlob == null) return Result(false, item.TicketError ?? "No se pudo generar el ticket");

                try
                {
                    await container.ReplaceItemAsync(
                        item, item.Id, new PartitionKey(item.Hub),
                        new ItemRequestOptions { IfMatchEtag = item.ETag }, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("No se pudo guardar el estado del ticket de {Folio}: {Tipo}", item.Folio, ex.GetType().Name);
                }

                uri = await _tickets.GetReadUriAsync(item.Folio, VigenciaEnlaceVista, ct);
                if (uri == null) return Result(false, "No se pudo abrir el ticket");
            }

            return Result(true, "OK", new { url = uri.ToString(), expira_en_minutos = (int)VigenciaEnlaceVista.TotalMinutes });
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [HttpPost("{folio}/aprobar")]
    [Authorize(Roles = AppRoles.Resolver)]
    public async Task<IActionResult> Approve(string folio, CancellationToken ct)
    {
        try
        {
            return await ResolveAsync(folio, item =>
            {
                item.Estatus = EstatusSolicitud.Aprobada;
                item.MinutosAprobados = item.MinutosSolicitados;
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
            // El motivo le llega al conductor por Valeria IA, por eso es obligatorio
            var note = input?.Note?.Trim();
            if (string.IsNullOrWhiteSpace(note))
                return Result(false, "El motivo del rechazo es obligatorio");
            if (note.Length > MaxNoteLength)
                return Result(false, $"El motivo no puede pasar de {MaxNoteLength} caracteres");

            return await ResolveAsync(folio, item =>
            {
                item.Estatus = EstatusSolicitud.Rechazada;
                item.MinutosAprobados = 0;
                item.Nota = note;
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
                var requested = item.MinutosSolicitados;
                if (input.Minutes <= 0 || input.Minutes >= requested)
                    return $"Los minutos aprobados deben ser mayores a 0 y menores a {requested}";

                item.Estatus = EstatusSolicitud.Parcial;
                item.MinutosAprobados = input.Minutes;
                item.Nota = note;
                return null;
            }, "Justificacion aprobada parcialmente", ct);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }
}
