using SkiaSharp;

namespace VemoLab.Api;

// Dibuja el ticket (comprobante) de una solicitud ya resuelta como imagen PNG.
// Usa SkiaSharp y la fuente Liberation Sans incrustada en el proyecto (carpeta Fonts):
// App Service Linux no trae fuentes del sistema, asi que no se puede depender de ellas.
// No lleva telefono ni conversation_id: es lo que ve el conductor.
public static class TicketImage
{
    private const int LogicalWidth = 600;
    private const int Scale = 2;              // el PNG sale al doble de resolucion para verse nitido en el celular
    private const float Pad = 36f;
    private const int MaxLogicalHeight = 2400;

    private static readonly Lazy<SKTypeface> Regular = new(() => LoadFont("Fonts.LiberationSans-Regular.ttf"));
    private static readonly Lazy<SKTypeface> Bold = new(() => LoadFont("Fonts.LiberationSans-Bold.ttf"));

    private static readonly SKColor Ink = SKColor.Parse("#13202B");
    private static readonly SKColor Muted = SKColor.Parse("#56666F");
    private static readonly SKColor LineColor = SKColor.Parse("#D6DEE2");
    private static readonly SKColor White = SKColor.Parse("#FFFFFF");

    public static byte[] Render(Solicitud s)
    {
        if (s.Estatus == EstatusSolicitud.Pendiente)
            throw new InvalidOperationException("Solo se genera ticket de solicitudes ya resueltas");

        var (decision, bandHex) = s.Estatus switch
        {
            EstatusSolicitud.Aprobada => ("APROBADA", "#0E7C6B"),
            EstatusSolicitud.Parcial => ("APROBACIÓN PARCIAL", "#2D5C9E"),
            _ => ("NO APROBADA", "#B23E2B")
        };

        var aprobado = s.Estatus == EstatusSolicitud.Rechazada
            ? "Ninguno"
            : FormatMinutes(s.MinutosAprobados ?? 0);

        var horario = string.Join(" - ", new[] { s.HoraInicio, s.HoraFin }
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!.Trim()));

        var rows = new List<(string Label, string Value)>
        {
            ("Conductor", s.NombreCompleto),
            ("Placas", s.Placas),
            ("Hub", s.Hub),
            ("Categoría", s.Categoria),
            ("Horario", horario),
            ("Tiempo solicitado", FormatMinutes(s.MinutosSolicitados)),
            ("Tiempo aprobado", aprobado),
            ("Resolvió", s.ResueltoPor ?? "-")
        };
        if (!string.IsNullOrWhiteSpace(s.Nota))
            rows.Add((s.Estatus == EstatusSolicitud.Rechazada ? "Motivo del rechazo" : "Nota", s.Nota!));

        using var fSmallCaps = new SKFont(Bold.Value, 14);
        using var fDecision = new SKFont(Bold.Value, 30);
        using var fFolio = new SKFont(Bold.Value, 22);
        using var fMeta = new SKFont(Regular.Value, 15);
        using var fLabel = new SKFont(Regular.Value, 13);
        using var fValue = new SKFont(Regular.Value, 18);
        using var fFooter = new SKFont(Regular.Value, 12);

        using var paintBand = new SKPaint { Color = SKColor.Parse(bandHex), IsAntialias = true };
        using var paintWhite = new SKPaint { Color = White, IsAntialias = true };
        using var paintInk = new SKPaint { Color = Ink, IsAntialias = true };
        using var paintMuted = new SKPaint { Color = Muted, IsAntialias = true };
        using var paintLine = new SKPaint { Color = LineColor, StrokeWidth = 1f, Style = SKPaintStyle.Stroke };

        var textWidth = LogicalWidth - 2 * Pad;
        var wrapped = rows
            .Select(r => (r.Label, Lines: Wrap(string.IsNullOrWhiteSpace(r.Value) ? "-" : r.Value, fValue, textWidth)))
            .ToList();

        // Se recorre dos veces: la primera (sin lienzo) solo mide el alto; la segunda dibuja
        float Layout(SKCanvas? c)
        {
            const float bandHeight = 104f;
            c?.Clear(White);
            c?.DrawRect(0, 0, LogicalWidth, bandHeight, paintBand);
            c?.DrawText("COMPROBANTE DE JUSTIFICACIÓN", Pad, 38, fSmallCaps, paintWhite);
            c?.DrawText(decision, Pad, 78, fDecision, paintWhite);

            var y = bandHeight + 38;
            c?.DrawText($"Folio {s.Folio}", Pad, y, fFolio, paintInk);
            y += 24;
            c?.DrawText("Resuelta el " + (s.ResueltoEl ?? "-"), Pad, y, fMeta, paintMuted);
            y += 22;
            c?.DrawLine(Pad, y, LogicalWidth - Pad, y, paintLine);
            y += 30;

            foreach (var (label, lines) in wrapped)
            {
                c?.DrawText(label, Pad, y, fLabel, paintMuted);
                y += 22;
                foreach (var line in lines)
                {
                    c?.DrawText(line, Pad, y, fValue, paintInk);
                    y += 25;
                }
                y += 12;
            }

            y += 4;
            c?.DrawLine(Pad, y, LogicalWidth - Pad, y, paintLine);
            y += 26;
            c?.DrawText("Documento generado automáticamente", Pad, y, fFooter, paintMuted);
            return y + 28;
        }

        var height = Math.Min(MaxLogicalHeight, (int)Math.Ceiling(Layout(null)));
        using var surface = SKSurface.Create(new SKImageInfo(LogicalWidth * Scale, height * Scale));
        var canvas = surface.Canvas;
        canvas.Scale(Scale);
        Layout(canvas);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Mismo formato que la pagina: "1 h 30 min"
    private static string FormatMinutes(int minutes)
    {
        var h = minutes / 60;
        var m = minutes % 60;
        if (h == 0) return m + " min";
        if (m == 0) return h + " h";
        return h + " h " + m + " min";
    }

    // Parte el texto en lineas que quepan en el ancho; una palabra mas ancha que la linea se parte por caracteres
    private static List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", string.Empty).Split('\n'))
        {
            var current = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (font.MeasureText(candidate) <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = string.Empty;
                }

                var rest = word;
                while (rest.Length > 1 && font.MeasureText(rest) > maxWidth)
                {
                    var n = rest.Length - 1;
                    while (n > 1 && font.MeasureText(rest[..n]) > maxWidth) n--;
                    lines.Add(rest[..n]);
                    rest = rest[n..];
                }
                current = rest;
            }
            lines.Add(current);
        }
        return lines;
    }

    // Lee la fuente incrustada a memoria (el flujo del recurso no puede quedar abierto para Skia)
    private static SKTypeface LoadFont(string resource)
    {
        using var stream = typeof(TicketImage).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Falta la fuente incrustada {resource}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        using var data = SKData.CreateCopy(memory.ToArray());
        return SKTypeface.FromData(data)
            ?? throw new InvalidOperationException($"No se pudo leer la fuente {resource}");
    }
}
