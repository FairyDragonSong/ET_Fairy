using System;
using MongoDB.Driver;

namespace ET.Server
{
    public static class ProductionStartupValidator
    {
        public static void ValidateRealm(Options options, DatabaseConnectionSettings database)
        {
            if (options.Develop != 0)
            {
                return;
            }

            if (options.AccountAutoRegister == 1)
            {
                throw new InvalidOperationException("AccountAutoRegister cannot be enabled in production mode");
            }

            if (options.PasswordLoginEnabled == 1 && options.PasswordLoginTransportSecure != 1)
            {
                throw new InvalidOperationException(
                    "Password login requires an explicitly verified encrypted client transport in production mode");
            }

            if (!database.ConnectionFromEnvironment)
            {
                throw new InvalidOperationException("production MongoDB connection must be injected through ET_MONGO_URI[_ZONE_n]");
            }

            if (string.IsNullOrWhiteSpace(database.DatabaseName))
            {
                throw new InvalidOperationException("production MongoDB database name is empty");
            }

            MongoUrl mongoUrl = MongoUrl.Create(database.ConnectionString);
            if (!mongoUrl.HasAuthenticationSettings || string.IsNullOrWhiteSpace(mongoUrl.Username))
            {
                throw new InvalidOperationException("production MongoDB connection must use authentication");
            }

            if (!mongoUrl.UseTls || mongoUrl.AllowInsecureTls)
            {
                throw new InvalidOperationException("production MongoDB connection must use verified TLS");
            }

            bool isSrv = database.ConnectionString.StartsWith("mongodb+srv://", StringComparison.OrdinalIgnoreCase);
            if (!isSrv && string.IsNullOrWhiteSpace(mongoUrl.ReplicaSetName) && !mongoUrl.LoadBalanced)
            {
                throw new InvalidOperationException("production MongoDB must use a replica set, SRV cluster, or load-balanced deployment");
            }

            if (mongoUrl.RetryWrites == false)
            {
                throw new InvalidOperationException("production MongoDB retryWrites cannot be disabled");
            }
        }
    }
}
