namespace Nevermind.Network
{
    public static class MessageCode
    {
        public const int Hello = 0x00;
        public const int Disconnect = 0x01;
        public const int Ping = 0x02;
        public const int Pong = 0x03;
        public const int GetPeers = 0x04;
        public const int Peers = 0x05;
        public const int User = 0x0f;
    }
}