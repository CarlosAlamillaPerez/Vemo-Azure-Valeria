using System.Net.Http.Json;
using VemoLab.Api.Controllers;

namespace VemoLab.Api;

// Manda la decision tomada al flujo de Power Automate, que crea el elemento en la lista de SharePoint.
// La URL del flujo es secreta (Sharepoint:FlowUrl): nunca va en codigo ni en logs ni en respuestas.
public class SharePointSync
{
    // FASE DE PRUEBA: quitar estas marcas y los valores fijos al conectar con datos reales
    private const string TestMarker = "[PRUEBA] ";
    private const string TestValue = "PRUEBA";

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

        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            using var response = await client.PostAsJsonAsync(url, BuildPayload(item), ct);
            if (response.IsSuccessStatusCode) return (true, null);

            _logger.LogError("El flujo de SharePoint respondio {Status} para {Folio}",
                (int)response.StatusCode, item.Folio);
            return (false, $"El flujo respondio {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            // Solo el tipo de error: el mensaje de una excepcion HTTP puede incluir la direccion
            _logger.LogError("No se pudo llamar al flujo de SharePoint para {Folio}: {Tipo}",
                item.Folio, ex.GetType().Name);
            return (false, "No se pudo conectar con el flujo de SharePoint");
        }
    }

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
