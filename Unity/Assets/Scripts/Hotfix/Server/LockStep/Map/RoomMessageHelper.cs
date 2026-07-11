namespace ET.Server
{

    public static partial class RoomMessageHelper
    {
        public static void BroadCast(Room room, IMessage message)
        {
            // 广播的消息不能被池回收
            (message as MessageObject).IsFromPool = false;
            
            RoomServerComponent roomServerComponent = room.GetComponent<RoomServerComponent>();

            foreach (var kv in roomServerComponent.Children)
            {
                RoomPlayer roomPlayer = kv.Value as RoomPlayer;

                if (!roomPlayer.IsOnline)
                {
                    continue;
                }
                
                room.Root().NotifyEntity(EntityAddress.From(LocationType.GateSession, roomPlayer.Id), message);
            }
        }
    }
}
