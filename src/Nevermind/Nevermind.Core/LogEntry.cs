namespace Nevermind.Core
{
    public class LogEntry
    {
        public LogEntry(Address address, byte[] data, byte[][] topics)
        {
            LoggersAddress = address;
            Data = data;
            Topics = topics;
        }

        public Address LoggersAddress { get; }
        public byte[][] Topics { get; }
        public byte[] Data { get; }
    }
}