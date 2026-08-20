namespace ET
{
    public static partial class ConstValue
    {
        // 出包时 这里得改 不可以是本地IP
        public const string RouterHttpHost = "127.0.0.1"; 
        public const int RouterHttpPort = 30300;
        public const int SessionTimeoutTime = 30 * 1000;
        
        
    }
}