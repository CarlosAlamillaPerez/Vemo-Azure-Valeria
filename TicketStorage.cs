using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace VemoLab.Api;

// Guarda los tickets (PNG) en un contenedor PRIVADO de Blob Storage y genera enlaces temporales de lectura.
// La cadena de conexion es secreta (Storage:ConnectionString): nunca va en codigo, logs ni respuestas.
// Los enlaces llevan firma (SAS): quien los tenga puede ver ESE archivo hasta que caducan.
public class TicketStorage
{
    private const string DefaultContainer = "tickets";

    private readonly IConfiguration _config;
    private readonly SemaphoreSlim _init = new(1, 1);
    private BlobContainerClient? _container;

    public TicketStorage(IConfiguration config)
    {
        _config = config;
    }

    public static string BlobName(long folio) => $"{folio}.png";

    // Dias que vive el enlace que se manda a Valeria IA (por defecto 3)
    public int DiasVigenciaEnlace => Math.Clamp(_config.GetValue("Storage:DiasVigenciaEnlace", 3), 1, 7);

    private async Task<BlobContainerClient> GetContainerAsync(CancellationToken ct)
    {
        if (_container != null) return _container;

        await _init.WaitAsync(ct);
        try
        {
            if (_container == null)
            {
                var connectionString = _config["Storage:ConnectionString"];
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new InvalidOperationException("Falta la configuracion Storage:ConnectionString");

                // Error comun: copiar la "Clave" en vez de la "Cadena de conexion" (igual que el Bug 5 de Cosmos)
                if (!connectionString.Contains("AccountName=", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Storage:ConnectionString no parece una cadena de conexion: debe empezar con " +
                        "DefaultEndpointsProtocol=https;AccountName=... (¿se copio la clave en vez de la cadena?)");

                var name = _config["Storage:Container"];
                var client = new BlobContainerClient(
                    connectionString, string.IsNullOrWhiteSpace(name) ? DefaultContainer : name.Trim());

                // Privado: nunca se pide acceso anonimo (la cuenta ademas lo tiene deshabilitado)
                await client.CreateIfNotExistsAsync(cancellationToken: ct);
                _container = client;
            }
            return _container;
        }
        finally
        {
            _init.Release();
        }
    }

    // Sube (o reemplaza) el ticket de un folio
    public async Task UploadAsync(long folio, byte[] png, CancellationToken ct)
    {
        var container = await GetContainerAsync(ct);
        var blob = container.GetBlobClient(BlobName(folio));
        using var stream = new MemoryStream(png);
        await blob.UploadAsync(
            stream,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "image/png" } },
            ct);
    }

    // Enlace de lectura que caduca en "vigencia". Devuelve null si el archivo ya no existe.
    public async Task<Uri?> GetReadUriAsync(long folio, TimeSpan vigencia, CancellationToken ct)
    {
        var container = await GetContainerAsync(ct);
        var blob = container.GetBlobClient(BlobName(folio));

        if (!(await blob.ExistsAsync(ct)).Value) return null;

        if (!blob.CanGenerateSasUri)
            throw new InvalidOperationException("La cadena de conexion no permite firmar enlaces (falta AccountKey)");

        var sas = new BlobSasBuilder(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(vigencia))
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5)   // tolera diferencias de reloj
        };
        return blob.GenerateSasUri(sas);
    }

    public async Task DeleteAsync(long folio, CancellationToken ct)
    {
        var container = await GetContainerAsync(ct);
        await container.GetBlobClient(BlobName(folio)).DeleteIfExistsAsync(cancellationToken: ct);
    }
}
