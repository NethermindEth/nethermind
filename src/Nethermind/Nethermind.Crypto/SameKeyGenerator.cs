namespace Nethermind.Crypto
{
    public class SameKeyGenerator(PrivateKey privateKey) : IPrivateKeyGenerator
    {
        private readonly PrivateKey _privateKey = privateKey;

        public PrivateKey Generate()
        {
            return _privateKey;
        }
    }
}
