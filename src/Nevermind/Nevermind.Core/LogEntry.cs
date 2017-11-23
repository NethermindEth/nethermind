using Nevermind.Core.Crypto;

namespace Nevermind.Core
{
    public class LogEntry
    {
        public LogEntry(Address address, byte[] data, Keccak[] topics)
        {
            LoggersAddress = address;
            Data = data;
            Topics = topics;
        }

        public Address LoggersAddress { get; }
        public Keccak[] Topics { get; }
        public byte[] Data { get; }
    }
}