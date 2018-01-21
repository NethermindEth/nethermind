namespace Nevermind.Discovery.Messages
{
    public abstract class Message
    {
        public byte[] Content { get; set; }

        public byte[] Mdc { get; set; }
        public byte[] Signature { get; set; }
        public byte[] Type { get; set; }
        public byte[] Data { get; set; }

        public byte[] GetNodeId()
        {
            //TODO recover public key from signature
            return Signature;
        }
    }
}
