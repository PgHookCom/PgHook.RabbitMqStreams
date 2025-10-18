using PgOutput2Json;
using System.Net;
using System.Net.Sockets;

namespace PgHook.RabbitMqStreams
{
    public class Worker(IConfiguration cfg, ILoggerFactory loggerFactory) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var rmqEndPoints = cfg.GetValue<string>("PGH_RMQ_ENDPOINTS") ?? "";
            if (string.IsNullOrWhiteSpace(rmqEndPoints))
            {
                throw new Exception("PGH_RMQ_ENDPOINTS is not set");
            }

            var rmqUsername = cfg.GetValue<string>("PGH_RMQ_USERNAME") ?? "";
            if (string.IsNullOrWhiteSpace(rmqUsername))
            {
                throw new Exception("PGH_RMQ_USERNAME is not set");
            }

            var rmqPassword = cfg.GetValue<string>("PGH_RMQ_PASSWORD") ?? "";
            if (string.IsNullOrWhiteSpace(rmqPassword))
            {
                throw new Exception("PGH_RMQ_PASSWORD is not set");
            }
            
            var rmqVirtualHost = cfg.GetValue<string>("PGH_RMQ_VHOST") ?? "/";

            var rmqStreamName = cfg.GetValue<string>("PGH_RMQ_STREAM_NAME") ?? "";
            if (string.IsNullOrWhiteSpace(rmqStreamName))
            {
                throw new Exception("PGH_RMQ_STREAM_NAME is not set");
            }

            var rmqIsSuperStream = cfg.GetValue<bool>("PGH_RMQ_IS_SUPER_STREAM");

            var partitionKeyFields = new Dictionary<string, List<string>>();

            for (var i = 1; true; i++)
            {
                var keyFieldsString = cfg.GetValue<string>($"PGH_RMQ_PARTITION_KEY_FIELDS_{i}");
                if (string.IsNullOrWhiteSpace(keyFieldsString)) break;

                AddPartitionKeyFields(partitionKeyFields, keyFieldsString);
            }

            var connectionString = cfg.GetValue<string>("PGH_POSTGRES_CONN") ?? "";
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new Exception("PGH_POSTGRES_CONN is not set");
            }

            var publicationNamesCfg = cfg.GetValue<string>("PGH_PUBLICATION_NAMES") ?? "";
            if (string.IsNullOrWhiteSpace(publicationNamesCfg))
            {
                throw new Exception("PGH_PUBLICATION_NAMES is not set");
            }

            string[] publicationNames = [.. publicationNamesCfg.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())];

            var usePermanentSlot = cfg.GetValue<bool>("PGH_USE_PERMANENT_SLOT");

            var replicationSlot = cfg.GetValue<string>("PGH_REPLICATION_SLOT") ?? "";
            if (string.IsNullOrWhiteSpace(replicationSlot))
            {
                if (usePermanentSlot)
                {
                    throw new Exception("PGH_REPLICATION_SLOT is not set");
                }
                else
                {
                    replicationSlot = $"pghook_{Guid.NewGuid().ToString().Replace("-", "")}";
                }
            }

            var batchSize = cfg.GetValue<int>("PGH_BATCH_SIZE");
            if (batchSize < 1)
            {
                batchSize = 100;
            }

            var useCompactJson = cfg.GetValue<bool>("PGH_JSON_COMPACT");

            var pgOutput2JsonBuilder = PgOutput2JsonBuilder.Create()
                .WithLoggerFactory(loggerFactory)
                .WithPgConnectionString(connectionString)
                .WithPgPublications(publicationNames)
                .WithPgReplicationSlot(replicationSlot, useTemporarySlot: !usePermanentSlot)
                .WithBatchSize(batchSize)
                .WithJsonOptions(jsonOptions =>
                {
                    jsonOptions.WriteTableNames = true;
                    jsonOptions.WriteTimestamps = true;
                    jsonOptions.TimestampFormat = TimestampFormat.UnixTimeMilliseconds;
                    jsonOptions.WriteMode = useCompactJson ? JsonWriteMode.Compact : JsonWriteMode.Default;
                })
                .UseRabbitMqStreams(options =>
                {
                    options.IsSuperStream = rmqIsSuperStream;
                    options.StreamName = rmqStreamName;

                    options.StreamSystemConfig.Endpoints = ParseRmqEndPoints(rmqEndPoints);
                    options.StreamSystemConfig.UserName = rmqUsername;
                    options.StreamSystemConfig.Password = rmqPassword;
                    options.StreamSystemConfig.VirtualHost = rmqVirtualHost;
                });

            foreach (var (tableName, fields) in partitionKeyFields)
            {
                if (fields.Count > 0)
                {
                    pgOutput2JsonBuilder.WithPartitionKeyFields(tableName, fields[0], [.. fields.Skip(1)]);
                }
            }

            using var pgOutput2Json = pgOutput2JsonBuilder
                .Build();

            await pgOutput2Json.StartAsync(stoppingToken);
        }

        private static void AddPartitionKeyFields(Dictionary<string, List<string>> keyFields, string keyFieldsString)
        {
            var parts = keyFieldsString.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 2) throw new Exception("Invalid key fields string: " + keyFieldsString);

            var tableName = parts[0];

            List<string> fields = [.. parts[1]
                .Trim()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            
            keyFields.Add(tableName, fields);
        }

        private static List<EndPoint> ParseRmqEndPoints(string rmqEndPoints)
        {
            var endpoints = new List<EndPoint>();

            var addresses = rmqEndPoints.Split(',');

            foreach (var address in addresses)
            {
                var trimmed = address.Trim();
                var parts = trimmed.Split(':');

                var host = parts[0];

                int port;
                if (parts.Length > 1)
                {
                    if (!int.TryParse(parts[1], out port))
                    {
                        throw new ArgumentException($"Invalid port in endpoint: '{address}'. Port must be a valid integer");
                    }
                }
                else
                {
                    port = 5552;
                }

                var ipAddresses = Dns.GetHostAddresses(host)
                    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                    .ToList();

                if (ipAddresses.Count == 0)
                {
                    throw new ArgumentException($"Could not resolve any IPv4 addresses for hostname: '{host}'");
                }

                foreach (var ipAddress in ipAddresses)
                {
                    endpoints.Add(new IPEndPoint(ipAddress, port));
                }
            }

            return endpoints;
        }
    }
}
