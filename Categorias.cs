namespace VemoLab.Api;

// Catalogo de categorias de la lista Soporte Vemo (columna categoria), tal como las paso Carlos.
// Los textos van EXACTOS, con sus acentos o su falta de ellos: SharePoint compara el texto tal cual.
// Valeria IA manda la categoria en "categoria" y la API la pasa a SharePoint sin cambiarla.
public static class Categorias
{
    public static readonly IReadOnlyList<string> Todas = new[]
    {
        "Pinchadura",
        "Otra",
        "Batería",
        "Limpieza de Vehículo",
        "Servicio",
        "Vehículo con Desperfecto / Falla Mecánica",
        "Siniestro - Responsable",
        "Siniestro - Afectado",
        "Envio a taller",
        "Vehiculo con Desperfecto / Envio a taller",
        "Justificar tiempo por entrega de unidad tardia por falta de carga",
        "Justificar tiempo por falla temporal en la unidad",
        "Justificar tiempo por entrega tardia de unidad por parte del HUB",
        "Justificar tiempo por taller",
        "Justificar tiempo por evento interno",
        "Justificar tiempos por siniestro",
        "Justificar tiempo por solicitud de RRHH/Sindicato",
        "Justificar tiempos por pinchadura",
        "Justificar tiempos, varios.",
        "Justificar tiempo por Re entrenamiento",
        "Justificar tiempo por cobro por llanta o siniestro con generalista",
        "Justificar tiempo por sesión sindical",
        "Justificar tiempo por no llegar a la cita"
    };

    private static readonly HashSet<string> Catalogo = new(Todas, StringComparer.OrdinalIgnoreCase);

    // Ignora mayusculas pero no acentos: "Bateria" no es "Batería" para SharePoint
    public static bool EsConocida(string? categoria) =>
        !string.IsNullOrWhiteSpace(categoria) && Catalogo.Contains(categoria.Trim());
}
