using System;

namespace ET.Server
{
    public readonly struct DatabaseConnectionSettings
    {
        public string ConnectionString { get; }
        public string DatabaseName { get; }
        public bool ConnectionFromEnvironment { get; }

        public DatabaseConnectionSettings(string connectionString, string databaseName, bool connectionFromEnvironment)
        {
            this.ConnectionString = connectionString;
            this.DatabaseName = databaseName;
            this.ConnectionFromEnvironment = connectionFromEnvironment;
        }
    }

    public static class DatabaseConnectionResolver
    {
        public static DatabaseConnectionSettings Resolve(int zone)
        {
            StartZoneConfig config = StartZoneConfigCategory.Instance.Get(zone);
            string zoneConnection = Environment.GetEnvironmentVariable($"ET_MONGO_URI_ZONE_{zone}");
            string sharedConnection = Environment.GetEnvironmentVariable("ET_MONGO_URI");
            string connection = string.IsNullOrWhiteSpace(zoneConnection) ? sharedConnection : zoneConnection;
            bool fromEnvironment = !string.IsNullOrWhiteSpace(connection);
            if (!fromEnvironment)
            {
                connection = config.DBConnection;
            }

            string zoneDatabase = Environment.GetEnvironmentVariable($"ET_MONGO_DATABASE_ZONE_{zone}");
            string sharedDatabase = Environment.GetEnvironmentVariable("ET_MONGO_DATABASE");
            string databaseName = string.IsNullOrWhiteSpace(zoneDatabase) ? sharedDatabase : zoneDatabase;
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                databaseName = config.DBName;
            }

            return new DatabaseConnectionSettings(connection?.Trim() ?? string.Empty, databaseName?.Trim() ?? string.Empty, fromEnvironment);
        }
    }
}
