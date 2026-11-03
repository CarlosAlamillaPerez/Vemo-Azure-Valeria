using System.Collections.Concurrent;
using System.Net;
using Microsoft.Azure.Cosmos;

namespace VemoLab.Api;

// Crea el cliente de Cosmos, la base y los contenedores la primera vez que se necesitan.
// Si falta la cadena de conexion, el error llega como JSON desde el controller.
public class CosmosStore
{
    private const string DatabaseName = "valeria";
    private const int Throughput = 400;   // minimo del modo aprovisionado

    private const string JustificacionesName = "justificaciones";
    private const string JustificacionesPartition = "/hub";

    // El folio es un numero consecutivo. Cosmos no tiene secuencias: se lleva en un documento contador
    // dentro de "justificaciones" y se incrementa con control de concurrencia (IfMatchEtag).
    private const string ContadorHub = "_sistema";
    private const string ContadorId = "_contador_folio";
    private const long FolioInicial = 1000;   // el primer folio es 1001
    private const int ContadorIntentos = 10;

    private const string UsuariosName = "usuarios_roles";
    private const string UsuariosPartition = "/id";

    private readonly IConfiguration _config;
    private readonly SemaphoreSlim _init = new(1, 1);
    private readonly ConcurrentDictionary<string, Container> _containers = new();
    private CosmosClient? _client;
    private Database? _database;

    public CosmosStore(IConfiguration config)
    {
        _config = config;
    }

    public Task<Container> GetContainerAsync(CancellationToken ct)
        => EnsureContainerAsync(JustificacionesName, JustificacionesPartition, ct);

    public Task<Container> GetUsuariosContainerAsync(CancellationToken ct)
        => EnsureContainerAsync(UsuariosName, UsuariosPartition, ct);

    // Siguiente folio consecutivo. Si dos solicitudes llegan a la vez, una pierde la carrera (412 o 409),
    // vuelve a leer el contador y reintenta: nunca se entrega el mismo folio dos veces.
    public async Task<long> NextFolioAsync(CancellationToken ct)
    {
        var container = await GetContainerAsync(ct);
        var partition = new PartitionKey(ContadorHub);

        for (var intento = 0; intento < ContadorIntentos; intento++)
        {
            FolioContador contador;
            string? etag = null;
            try
            {
                var read = await container.ReadItemAsync<FolioContador>(ContadorId, partition, cancellationToken: ct);
                contador = read.Resource;
                etag = read.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                contador = new FolioContador { Valor = FolioInicial };
            }

            contador.Valor++;
            try
            {
                if (etag == null)
                    await container.CreateItemAsync(contador, partition, cancellationToken: ct);
                else
                    await container.ReplaceItemAsync(contador, ContadorId, partition,
                        new ItemRequestOptions { IfMatchEtag = etag }, ct);
                return contador.Valor;
            }
            catch (CosmosException ex) when (
                ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                // otro proceso se llevo ese numero: se vuelve a leer y se reintenta
            }
        }
        throw new InvalidOperationException("No se pudo generar el folio. Intenta de nuevo.");
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    // Busqueda directa por id + llave de particion (la operacion mas barata de Cosmos).
    // Devuelve null si la persona no esta en la lista.
    public async Task<UsuarioRol?> GetUsuarioAsync(string email, CancellationToken ct)
    {
        var id = NormalizeEmail(email);
        var container = await GetUsuariosContainerAsync(ct);
        try
        {
            var response = await container.ReadItemAsync<UsuarioRol>(id, new PartitionKey(id), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // Siembra el primer administrador. Idempotente: si ya existe, no lo toca.
    public async Task EnsureBootstrapAdminAsync(string email, CancellationToken ct)
    {
        var id = NormalizeEmail(email);
        var container = await GetUsuariosContainerAsync(ct);
        var admin = new UsuarioRol
        {
            Id = id,
            Nombre = "Administrador inicial",
            Roles = new List<string> { "admin", "aprobador" },
            Activo = true
        };
        try
        {
            await container.CreateItemAsync(admin, new PartitionKey(id), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // ya existia: se respeta lo que haya en Cosmos
        }
    }

    private async Task<Container> EnsureContainerAsync(string name, string partitionPath, CancellationToken ct)
    {
        if (_containers.TryGetValue(name, out var existing)) return existing;

        await _init.WaitAsync(ct);
        try
        {
            if (_containers.TryGetValue(name, out existing)) return existing;

            if (_database == null)
            {
                var connectionString = _config["Cosmos:ConnectionString"];
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new InvalidOperationException("Falta la configuracion Cosmos:ConnectionString");

                _client ??= new CosmosClient(connectionString, new CosmosClientOptions
                {
                    ConnectionMode = ConnectionMode.Gateway
                });

                var db = await _client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: ct);
                _database = db.Database;
            }

            var response = await _database.CreateContainerIfNotExistsAsync(
                new ContainerProperties(name, partitionPath), Throughput, cancellationToken: ct);

            _containers[name] = response.Container;
            return response.Container;
        }
        finally
        {
            _init.Release();
        }
    }
}
