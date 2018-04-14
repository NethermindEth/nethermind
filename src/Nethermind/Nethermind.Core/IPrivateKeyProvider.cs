using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public interface IPrivateKeyProvider
    {
        PrivateKey PrivateKey { get;}
    }
}