namespace Nevermind.KeyStore
{
    public class KDFParams
    {
        public int DkLen { get; set; }
        public int N { get; set; }
        public int P { get; set; }
        public int R { get; set; }
        public string Salt { get; set; }
    }
}