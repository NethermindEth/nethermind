namespace Nevermind.Network
{
    public class FrameHeaderData
    {
        public int ProtocolType { get; set; } // short
        public int? ContextId { get; set; } // short
        public int? TotalPacketSize { get; set; } // int
    }
}