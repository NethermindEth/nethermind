using System;
using Nevermind.Core.Crypto;

namespace Nevermind.Network
{
    public class EncryptionHandshakeServiceV4 : IEncryptionHandshakeService
    {
        public IAuthMessage InitiateAuth(PrivateKey privateKey, string hostName, int port)
        {
            throw new NotImplementedException();
        }

        public IAuthResponseMessage RespondToAuth(PrivateKey privateKey, IAuthMessage authMessage)
        {
            throw new NotImplementedException();
        }
    }
}