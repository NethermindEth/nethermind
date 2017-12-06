namespace Nevermind.Network
{
    public class Node
    {
        public int Port { get; set; }
        public string Host { get; set; }
        public NodePublicKey PublicKey { get; set; }
        
        // capabilities and client id here?
    }
}