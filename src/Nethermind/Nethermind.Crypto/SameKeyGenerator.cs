namespace Nethermind.Crypto
{
    public class SameKeyGenerator : IPrivateKeyGenerator
    {
        private readonly PrivateKey _privateKey;

        public SameKeyGenerator(PrivateKey privateKey)
        {
            _privateKey = privateKey;
        }

        public PrivateKey Generate()
        {
            return _privateKey;
        }
    }
}
