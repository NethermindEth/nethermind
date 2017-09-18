using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class LogEntry
    {
        public Address LoggersAddress { get; set; }
        public Keccak[] Topics { get; set; }
        public byte[] Data { get; set; }
    }
}