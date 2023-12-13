namespace Nethermind.Crypto
{
    public interface IProtectedPrivateKeyFactory
    {
        ProtectedPrivateKey Create(PrivateKey privateKey);
    }
}
