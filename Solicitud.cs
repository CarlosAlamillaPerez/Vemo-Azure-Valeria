using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nj = Newtonsoft.Json;

namespace VemoLab.Api;

public static class EstatusSolicitud
{
    public const string Pendiente = "pendiente";
    public const string Aprobada = "aprobada";
    public const string Parcial = "parcial";
    public const string Rechazada = "rechazada";
}

// Prefijos del conversation_id de las solicitudes de prueba (simuladas o cargadas desde el ORDS de prueba).
// Sirven para borrarlas sin tocar las de Valeria IA y para NO avisarles a Valeria (telefonos inventados).
public static class Pruebas
{
    public const string PrefijoSimulada = "sim-";
    public const string PrefijoOrds = "ords-";

    public static bool Es(string? conversationId) =>
        conversationId != null &&
        (conversationId.StartsWith(PrefijoSimulada, StringComparison.Ordinal) ||
         conversationId.StartsWith(PrefijoOrds, StringComparison.Ordinal));
}

// Una solicitud de justificacion que envia Valeria IA. Un documento por solicitud en "justificaciones".
// Campos planos, en espanol y snake_case: el documento se mapea casi uno a uno a una tabla relacional.
// El API (System.Text.Json) y Cosmos (Newtonsoft) necesitan cada uno su atributo, con el mismo nombre.
public class Solicitud
{
    public const string TipoDocSolicitud = "solicitud";

    // Cosmos exige "id": se usa el folio como texto. No se expone al cliente (ya va como "folio").
    [Nj.JsonProperty("id"), JsonIgnore]
    public string Id => Folio.ToString(CultureInfo.InvariantCulture);

    // Distingue las solicitudes del documento contador de folios, que vive en el mismo contenedor
    [JsonPropertyName("tipo_doc"), Nj.JsonProperty("tipo_doc")] public string TipoDoc { get; set; } = TipoDocSolicitud;

    [JsonPropertyName("folio"), Nj.JsonProperty("folio")] public long Folio { get; set; }
    [JsonPropertyName("estatus"), Nj.JsonProperty("estatus")] public string Estatus { get; set; } = EstatusSolicitud.Pendiente;

    // Lo que envia Valeria IA
    [JsonPropertyName("categoria"), Nj.JsonProperty("categoria")] public string Categoria { get; set; } = string.Empty;
    [JsonPropertyName("nombre_completo"), Nj.JsonProperty("nombre_completo")] public string NombreCompleto { get; set; } = string.Empty;
    [JsonPropertyName("hub"), Nj.JsonProperty("hub")] public string Hub { get; set; } = string.Empty;   // llave de particion
    [JsonPropertyName("placas"), Nj.JsonProperty("placas")] public string Placas { get; set; } = string.Empty;
    [JsonPropertyName("hora_inicio"), Nj.JsonProperty("hora_inicio")] public string? HoraInicio { get; set; }
    [JsonPropertyName("hora_fin"), Nj.JsonProperty("hora_fin")] public string? HoraFin { get; set; }
    [JsonPropertyName("tiempo_horas"), Nj.JsonProperty("tiempo_horas")] public int TiempoHoras { get; set; }
    [JsonPropertyName("tiempo_minutos"), Nj.JsonProperty("tiempo_minutos")] public int TiempoMinutos { get; set; }
    [JsonPropertyName("motivo_desconexion"), Nj.JsonProperty("motivo_desconexion")] public string? MotivoDesconexion { get; set; }

    // Datos para responder al conductor: se guardan pero NO se mandan al navegador
    [JsonIgnore, Nj.JsonProperty("numero_telefono")] public string NumeroTelefono { get; set; } = string.Empty;
    [JsonIgnore, Nj.JsonProperty("conversation_id")] public string ConversationId { get; set; } = string.Empty;   // clave unica de negocio

    // Lo que pone esta API
    [JsonPropertyName("minutos_solicitados"), Nj.JsonProperty("minutos_solicitados")] public int MinutosSolicitados { get; set; }
    [JsonPropertyName("recibido_el"), Nj.JsonProperty("recibido_el")] public string RecibidoEl { get; set; } = string.Empty;

    // Resolucion
    [JsonPropertyName("minutos_aprobados"), Nj.JsonProperty("minutos_aprobados")] public int? MinutosAprobados { get; set; }
    [JsonPropertyName("resuelto_por"), Nj.JsonProperty("resuelto_por")] public string? ResueltoPor { get; set; }
    [JsonPropertyName("resuelto_por_correo"), Nj.JsonProperty("resuelto_por_correo")] public string? ResueltoPorCorreo { get; set; }
    [JsonPropertyName("resuelto_el"), Nj.JsonProperty("resuelto_el")] public string? ResueltoEl { get; set; }
    [JsonPropertyName("nota"), Nj.JsonProperty("nota")] public string? Nota { get; set; }

    // Estado del envio a SharePoint: "sincronizado", "error" o vacio si todavia no aplica
    [JsonPropertyName("sharepoint_estatus"), Nj.JsonProperty("sharepoint_estatus")] public string? SharepointEstatus { get; set; }
    [JsonPropertyName("sharepoint_sincronizado_el"), Nj.JsonProperty("sharepoint_sincronizado_el")] public string? SharepointSincronizadoEl { get; set; }
    [JsonPropertyName("sharepoint_error"), Nj.JsonProperty("sharepoint_error")] public string? SharepointError { get; set; }

    // Ticket (imagen PNG) en Blob Storage: el nombre del archivo no se manda al navegador
    [JsonIgnore, Nj.JsonProperty("ticket_blob")] public string? TicketBlob { get; set; }
    [JsonPropertyName("ticket_generado_el"), Nj.JsonProperty("ticket_generado_el")] public string? TicketGeneradoEl { get; set; }
    [JsonPropertyName("ticket_error"), Nj.JsonProperty("ticket_error")] public string? TicketError { get; set; }

    // Aviso a Valeria IA: "notificado", "error", "no_configurado", "omitido_prueba" o vacio si todavia no aplica
    [JsonPropertyName("valeria_estatus"), Nj.JsonProperty("valeria_estatus")] public string? ValeriaEstatus { get; set; }
    [JsonPropertyName("valeria_notificado_el"), Nj.JsonProperty("valeria_notificado_el")] public string? ValeriaNotificadoEl { get; set; }
    [JsonPropertyName("valeria_error"), Nj.JsonProperty("valeria_error")] public string? ValeriaError { get; set; }

    // Control de concurrencia de Cosmos: evita resolver dos veces la misma solicitud
    [JsonIgnore, Nj.JsonProperty("_etag", NullValueHandling = Nj.NullValueHandling.Ignore)]
    public string? ETag { get; set; }
}

// Documento que lleva la cuenta de folios. Vive en el contenedor "justificaciones" con hub "_sistema".
public class FolioContador
{
    [Nj.JsonProperty("id")] public string Id { get; set; } = "_contador_folio";
    [Nj.JsonProperty("hub")] public string Hub { get; set; } = "_sistema";
    [Nj.JsonProperty("tipo_doc")] public string TipoDoc { get; set; } = "contador";
    [Nj.JsonProperty("valor")] public long Valor { get; set; }
}

// ---- Lo que Valeria IA manda a POST /api/justificaciones ----
// Todo nullable a proposito: la validacion y sus mensajes los hace el controller, para que Ale
// reciba "falta X" y no un error generico del framework.

public class SolicitudInput
{
    [JsonPropertyName("categoria"), JsonConverter(typeof(FlexibleStringConverter))] public string? Categoria { get; set; }
    [JsonPropertyName("nombre_completo"), JsonConverter(typeof(FlexibleStringConverter))] public string? NombreCompleto { get; set; }
    [JsonPropertyName("numero_telefono"), JsonConverter(typeof(FlexibleStringConverter))] public string? NumeroTelefono { get; set; }
    [JsonPropertyName("conversation_id"), JsonConverter(typeof(FlexibleStringConverter))] public string? ConversationId { get; set; }
    [JsonPropertyName("hub"), JsonConverter(typeof(FlexibleStringConverter))] public string? Hub { get; set; }
    [JsonPropertyName("horario")] public HorarioInput? Horario { get; set; }
    [JsonPropertyName("placas"), JsonConverter(typeof(FlexibleStringConverter))] public string? Placas { get; set; }
    [JsonPropertyName("tiempo_desconexion")] public TiempoInput? TiempoDesconexion { get; set; }
    [JsonPropertyName("motivo_desconexion"), JsonConverter(typeof(FlexibleStringConverter))] public string? MotivoDesconexion { get; set; }
}

public class HorarioInput
{
    [JsonPropertyName("hora_inicio"), JsonConverter(typeof(FlexibleStringConverter))] public string? HoraInicio { get; set; }
    [JsonPropertyName("hora_fin"), JsonConverter(typeof(FlexibleStringConverter))] public string? HoraFin { get; set; }
}

public class TiempoInput
{
    // Tambien acepta "1" entre comillas: un agente de IA a veces manda los numeros como texto
    [JsonPropertyName("horas"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public int Horas { get; set; }
    [JsonPropertyName("minutos"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public int Minutos { get; set; }
}

// Lee como texto un valor que puede llegar como texto o como numero (ej. un telefono sin comillas)
public class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(
                reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray()),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException("Se esperaba un texto")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
