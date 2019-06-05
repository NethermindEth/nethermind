namespace Nethermind.EthStats.Messages
{
    public class LatencyMessage : IMessage
    {
        public string Id { get; set; }
        public long Latency { get; }

        public LatencyMessage(long latency)
        {
            Latency = latency;
        }
    }
}