using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Discovery.Messages
{
    public class NodeIdResolver : INodeIdResolver
    {
        private readonly ISigner _signer;

        public NodeIdResolver(ISigner signer)
        {
            _signer = signer;
        }

        public PublicKey GetNodeId(byte[] signature, int recoveryId, byte[] messageType, byte[] data)
        {
            //return _signer.RecoverPublicKey(discoveryMessage.Signature, Keccak.Compute(Bytes.Concat(new[] {(byte)discoveryMessage.MessageType}, discoveryMessage.Payload)));
            return _signer.RecoverPublicKey(new Signature(signature, recoveryId), Keccak.Compute(Bytes.Concat(messageType, data)));
        }
    }
}