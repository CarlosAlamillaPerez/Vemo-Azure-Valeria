using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace VemoLab.Api;

// Avisa a Valeria IA que una solicitud ya fue contestada, con el enlace a la imagen del ticket.
// Queda APAGADO mientras no exista Valeria:NotifyUrl (el endpoint que nos pase Ale).
// El enlace (img_url) lleva una firma: nunca se registra en logs ni se guarda en Cosmos.
public class ValeriaNotifier
{
    public const string NoConfigurado = "no_configurado";
    public const string OmitidoPrueba = "omitido_prueba";
    public const string Notificado = "notificado";
    public const string Error = "error";

    private const int MaxStoredError = 400;

    private static readonly Regex SigPattern =
        new(@"sig=[^&""\s]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<ValeriaNotifier> _logger;

    public ValeriaNotifier(IHttpClientFactory http, IConfiguration config, ILogger<ValeriaNotifier> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    // Motivo por el que NO se debe avisar (null = si se avisa)
    public string? SkipReason(Solicitud item)
    {
        if (string.IsNullOrWhiteSpace(_config["Valeria:NotifyUrl"])) return NoConfigurado;

        // Las solicitudes de prueba traen telefonos inventados: avisarlas podria escribirle a una persona real
        if (Pruebas.Es(item.ConversationId) && !_config.GetValue("Valeria:NotificarPruebas", false))
            return OmitidoPrueba;

        return null;
    }

    public async Task<(bool Ok, string? Error)> SendAsync(Solicitud item, string? imgUrl, CancellationToken ct)
    {
        var url = _config["Valeria:NotifyUrl"];
        if (string.IsNullOrWhiteSpace(url)) return (false, "Falta la configuracion Valeria:NotifyUrl");
        if (string.IsNullOrWhiteSpace(imgUrl)) return (false, "No hay ticket que enviar");

        // Contrato acordado con Ale. motivo_rechazo solo va en los rechazos; en los demas casos, vacio.
        var payload = new Dictionary<string, object?>
        {
            ["numero_telefono"] = item.NumeroTelefono,
            ["conversation_id"] = item.ConversationId,
            ["motivo_rechazo"] = item.Estatus == EstatusSolicitud.Rechazada ? item.Nota ?? string.Empty : string.Empty,
            ["img_url"] = imgUrl
        };

        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            using var response = await client.PostAsJsonAsync(url, payload, ct);
            if (response.IsSuccessStatusCode) return (true, null);

            var body = Redact(await response.Content.ReadAsStringAsync(ct));
            _logger.LogError("Valeria IA respondio {Status} para el folio {Folio}. Respuesta: {Body}",
                (int)response.StatusCode, item.Folio, body);
            return (false, Truncate($"Valeria respondio {(int)response.StatusCode}. Respuesta: {body}"));
        }
        catch (Exception ex)
        {
            // Solo el tipo de error: el mensaje de una excepcion HTTP puede incluir direcciones
            _logger.LogError("No se pudo avisar a Valeria IA del folio {Folio}: {Tipo}", item.Folio, ex.GetType().Name);
            return (false, $"No se pudo conectar con Valeria ({ex.GetType().Name})");
        }
    }

    private static string Redact(string? text) =>
        string.IsNullOrEmpty(text) ? "(vacia)" : SigPattern.Replace(text, "sig=***");

    private static string Truncate(string text) =>
        text.Length <= MaxStoredError ? text : text[..MaxStoredError] + "...";
}
