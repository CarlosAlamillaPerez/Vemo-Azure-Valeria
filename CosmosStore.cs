using Microsoft.Azure.Cosmos;

namespace VemoLab.Api;

// Crea el cliente de Cosmos, la base y el contenedor la primera vez que se necesitan.
// Si falta la cadena de conexion, el error llega como JSON desde el controller.
public class CosmosStore
{
    private const string DatabaseName = "valeria";
    private const string ContainerName = "justificaciones";
    private const string PartitionPath = "/hub";
    private const int Throughput = 400;   // minimo del modo aprovisionado

    private readonly IConfiguration _config;
    private readonly SemaphoreSlim _init = new(1, 1);
    private CosmosClient? _client;
    private Container? _container;

    public CosmosStore(IConfiguration config)
    {
        _config = config;
    }

    public async Task<Container> GetContainerAsync(CancellationToken ct)
    {
        if (_container != null) return _container;

        await _init.WaitAsync(ct);
        try
        {
            if (_container == null)
            {
                var connectionString = _config["Cosmos:ConnectionString"];
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new InvalidOperationException("Falta la configuracion Cosmos:ConnectionString");

                _client ??= new CosmosClient(connectionString, new CosmosClientOptions
                {
                    ConnectionMode = ConnectionMode.Gateway
                });

                var db = await _client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: ct);
                var response = await db.Database.CreateContainerIfNotExistsAsync(
                    new ContainerProperties(ContainerName, PartitionPath), Throughput, cancellationToken: ct);

                _container = response.Container;
            }
            return _container;
        }
        finally
        {
            _init.Release();
        }
    }
}
