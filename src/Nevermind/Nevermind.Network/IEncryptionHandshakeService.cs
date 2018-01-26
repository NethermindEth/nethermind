
using Nevermind.Core.Crypto;

namespace Nevermind.Network
{
    public interface IEncryptionHandshakeService
    {
        IAuthMessage InitiateAuth(PrivateKey privateKey, string hostName, int port);
        IAuthResponseMessage RespondToAuth(PrivateKey privateKey, IAuthMessage authMessage);
    }
}