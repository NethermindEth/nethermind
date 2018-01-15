namespace Nevermind.KeyStore
{
    public class Crypto
    {
        public string Cipher { get; set; }
        public string CipherText { get; set; }
        public CipherParams CipherParams { get; set; }
        public string KDF { get; set; }
        public KDFParams KDFParams { get; set; }       
        public string MAC { get; set; }
        public int Version { get; set; }
    }
}