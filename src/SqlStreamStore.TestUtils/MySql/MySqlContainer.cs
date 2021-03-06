namespace SqlStreamStore.TestUtils.MySql
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Ductus.FluentDocker.Builders;
    using Ductus.FluentDocker.Services;
    using MySqlConnector;
    using Polly;
    using SqlStreamStore.Infrastructure;

    public class MySqlContainer
    {
        private const string Image = "mysql:5.6";
        private const string ContainerName = "sql-stream-store-tests-mysql";
        private const int ContainerPort = 3306;

        private readonly string _databaseName;
        private readonly uint _hostPort;
        private readonly IContainerService _containerService;

        public MySqlContainer(string databaseName, string name = ContainerName, uint hostPort = ContainerPort)
        {
            _databaseName = databaseName;
            _hostPort = hostPort;

            _containerService = new Builder()
                .UseContainer()
                .WithName(name)
                .UseImage(Image)
                .KeepRunning()
                .ReuseIfExists()
                .WithEnvironment("MYSQL_ALLOW_EMPTY_PASSWORD=1")
                .ExposePort((int)hostPort, ContainerPort)
                .Build();
        }

        public string ConnectionString => ConnectionStringBuilder.ConnectionString;

        public async Task Start(CancellationToken cancellationToken = default)
        {
            _containerService.Start();

            await Policy
                .Handle<MySqlException>()
                .WaitAndRetryAsync(30, _ => TimeSpan.FromMilliseconds(500))
                .ExecuteAsync(async () =>
                {
                    using(var connection = new MySqlConnection(DefaultConnectionString))
                    {
                        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                        // Crude way to work around the max_connections issue that occurs in heavy parallel tests
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = "set global max_connections = 1024;";

                            await command.ExecuteNonQueryAsync(cancellationToken);
                        }
                    }
                });
        }

        public async Task CreateDatabase(CancellationToken cancellationToken = default)
        {
            var commandText = $"CREATE DATABASE IF NOT EXISTS `{_databaseName}`";
            using (var connection = new MySqlConnection(DefaultConnectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var command = new MySqlCommand(commandText, connection))
                {
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private string DefaultConnectionString => new MySqlConnectionStringBuilder(ConnectionString)
            {
                Database = null,
                IgnorePrepare = false,
                Port = _hostPort,
            }.ConnectionString;

        private MySqlConnectionStringBuilder ConnectionStringBuilder => new MySqlConnectionStringBuilder
        {
            Database = _databaseName,
            Port = _hostPort,
            UserID = "root",
            Pooling = true,
            IgnorePrepare = false,
            MaximumPoolSize = 1000,
        };
    }
}
