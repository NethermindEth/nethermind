namespace Nevermind.Network
{
    public class EncryptionSecrets
    {
        public byte[] EgressMac { get; set; }
        public byte[] IngressMac { get; set; }
        public byte[] AesSecret { get; set; }
        public byte[] MacSecret { get; set; }
        public byte[] Token { get; set; }
    }
}