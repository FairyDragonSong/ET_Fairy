using System;

namespace ET.Server
{
    [FriendOf(typeof(DBManagerComponent))]
    public static partial class DBManagerComponentSystem
    {
        public static DBComponent GetZoneDB(this DBManagerComponent self, int zone)
        {
            DBComponent dbComponent = self.GetChild<DBComponent>(zone);
            if (dbComponent != null)
            {
                return dbComponent;
            }

            DatabaseConnectionSettings settings = DatabaseConnectionResolver.Resolve(zone);
            if (settings.ConnectionString == "")
            {
                throw new Exception($"zone: {zone} not found mongo connect string");
            }

            dbComponent = self.AddChildWithId<DBComponent, string, string>(
                zone,
                settings.ConnectionString,
                settings.DatabaseName);
            return dbComponent;
        }
    }
}
