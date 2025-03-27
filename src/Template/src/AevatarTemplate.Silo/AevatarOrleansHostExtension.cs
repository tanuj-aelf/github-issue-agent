using Aevatar.EventSourcing.MongoDB.Hosting;
using Aevatar.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace AevatarTemplate.Silo;

public static class AevatarOrleansHostExtension
{
    public static IHostBuilder UseOrleansConfiguration(this IHostBuilder hostBuilder)
    {
        return hostBuilder.UseOrleans((context, siloBuilder) =>
            {
                const string mongoDbDefaultConnectString = "mongodb://localhost:27017/?maxPoolSize=555";
                siloBuilder
                    .UseLocalhostClustering()
                    .UseMongoDBClient(mongoDbDefaultConnectString)
                    .AddMongoDBGrainStorage("PubSubStore", options =>
                    {
                        options.CollectionPrefix = "StreamStorage";
                        options.DatabaseName = "AevatarDb";
                    })
                    .ConfigureLogging(logging => { logging.SetMinimumLevel(LogLevel.Debug).AddConsole(); })
                    .AddMongoDbStorageBasedLogConsistencyProvider("LogStorage", options =>
                    {
                        options.ClientSettings =
                            MongoClientSettings.FromConnectionString(mongoDbDefaultConnectString);
                        options.Database = "AevatarDb";
                    })
                    .AddMemoryStreams("Aevatar")
                    .UseAevatar<AevatarSiloModule>();
            })
            .UseConsoleLifetime();
    }
}