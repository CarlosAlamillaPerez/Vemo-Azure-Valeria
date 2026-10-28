using System.Net.Http.Json;
using System.Text.RegularExpressions;
using VemoLab.Api.Controllers;

namespace VemoLab.Api;

// Manda la decision tomada al flujo de Power Automate, que crea el elemento en la lista de SharePoint.
// La URL del flujo es secreta (Sharepoint:FlowUrl): nunca va en codigo ni en logs ni en respuestas.
// Para diagnosticar solo se registran datos de la FORMA de la URL (host, largo, parametros presentes),
// nunca sus valores, y cualquier "sig=..." se tapa antes de registrar o guardar un texto.
public class SharePointSync
{
    // FASE DE PRUEBA: quitar estas marcas y los valores fijos al conectar con datos reales
    private const string TestMarker = "[PRUEBA] ";
    private const string TestValue = "PRUEBA";

    // Largo maximo del detalle que se guarda en sharepoint_error (el log lleva el detalle completo)
    private const int MaxStoredError = 600;

    private static readonly Regex SigPattern =
        new(@"sig=[^&""\s]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<SharePointSync> _logger;

    public SharePointSync(IHttpClientFactory http, IConfiguration config, ILogger<SharePointSync> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<(bool Ok, string? Error)> SendAsync(Justification item, CancellationToken ct)
    {
        var url = _config["Sharepoint:FlowUrl"];
        if (string.IsNullOrWhiteSpace(url))
            return (false, "Falta la configuracion Sharepoint:FlowUrl");

        var urlInfo = DescribeUrl(url);

        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            using var response = await client.PostAsJsonAsync(url, BuildPayload(item), ct);
            if (response.IsSuccessStatusCode) return (true, null);

            var body = Redact(await response.Content.ReadAsStringAsync(ct));

            _logger.LogError(
                "El flujo de SharePoint respondio {Status} para {Folio}. URL: {UrlInfo}. Respuesta: {Body}",
                (int)response.StatusCode, item.Folio, urlInfo, body);

            return (false, Truncate($"El flujo respondio {(int)response.StatusCode}. URL: {urlInfo}. Respuesta: {body}"));
        }
        catch (Exception ex)
        {
            // Mensaje con la firma tapada: el mensaje de una excepcion HTTP puede incluir la direccion
            var message = Redact(ex.Message);
            _logger.LogError("No se pudo llamar al flujo de SharePoint para {Folio}: {Tipo} - {Mensaje}. URL: {UrlInfo}",
                item.Folio, ex.GetType().Name, message, urlInfo);
            return (false, Truncate($"No se pudo conectar con el flujo ({ex.GetType().Name}: {message}). URL: {urlInfo}"));
        }
    }

    // Describe la URL sin exponerla: host, largo, si trae espacios/comillas/escapes y que parametros tiene
    private static string DescribeUrl(string url)
    {
        var flags = new List<string> { $"largo={url.Length}" };

        if (url != url.Trim()) flags.Add("ESPACIOS_AL_INICIO_O_FINAL");
        if (url.Contains('"') || url.Contains('\'')) flags.Add("TRAE_COMILLAS");
        if (url.Contains("\\u0026", StringComparison.OrdinalIgnoreCase)) flags.Add("TRAE_\\u0026_EN_VEZ_DE_&");
        if (url.Contains("&amp;", StringComparison.OrdinalIgnoreCase)) flags.Add("TRAE_&amp;_EN_VEZ_DE_&");

        if (Uri.TryCreate(url.Trim().Trim('"', '\''), UriKind.Absolute, out var uri))
        {
            flags.Add($"host={uri.Host}");
            var names = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=')[0]);
            flags.Add($"parametros=[{string.Join(",", names)}]");
        }
        else
        {
            flags.Add("NO_ES_URL_VALIDA");
        }

        return string.Join(" ", flags);
    }

    private static string Redact(string? text) =>
        string.IsNullOrEmpty(text) ? "(vacia)" : SigPattern.Replace(text, "sig=***");

    private static string Truncate(string text) =>
        text.Length <= MaxStoredError ? text : text[..MaxStoredError] + "...";

    // Mismos nombres que el esquema JSON del desencadenador del flujo
    private static Dictionary<string, object?> BuildPayload(Justification item)
    {
        var lostMinutes = item.RequestedMinutes ?? 0;   // tiempo que perdio el conductor, sin importar la decision

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Reason)) parts.Add(item.Reason.Trim());
        if (!string.IsNullOrWhiteSpace(item.Note)) parts.Add("Nota: " + item.Note.Trim());

        var dia = item.ReceivedAt is { Length: >= 10 } received
            ? received[..10]
            : DateTime.UtcNow.ToString("yyyy-MM-dd");

        return new Dictionary<string, object?>
        {
            ["sistema_id"] = item.Folio,                                    // pendiente: id numerico del sistema origen
            ["conductor"] = (item.DriverName ?? string.Empty).ToUpperInvariant(),
            ["conductor_justificado"] = item.Status switch
            {
                "aprobada" => "Si",
                "parcial" => "Parcial",
                "rechazada" => "No",
                _ => "A revisar"
            },
            ["pais"] = "Mexico",
            ["pais_id"] = TestValue,                                        // pendiente: no tenemos este dato
            ["turno"] = item.ShiftStart ?? string.Empty,                    // pendiente: falta la hora de fin
            ["dia"] = dia,                                                  // pendiente: dia del turno, hoy es la fecha de recepcion
            ["horas_perdidas"] = Math.Round(lostMinutes / 60.0, 2),
            ["minutos_perdidos"] = lostMinutes,
            ["categoria"] = TestValue,                                      // pendiente: catalogo de categorias
            ["observaciones"] = TestMarker + string.Join(" | ", parts),
            ["hub"] = item.Hub ?? string.Empty
        };
    }
}
