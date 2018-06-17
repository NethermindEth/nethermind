using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Model;

namespace Nethermind.Network.Discovery.Messages
{
    public class NodeIdResolver : INodeIdResolver
    {
        private readonly ISigner _signer;

        public NodeIdResolver(ISigner signer)
        {
            _signer = signer;
        }

        public NodeId GetNodeId(byte[] signature, int recoveryId, byte[] messageType, byte[] data)
        {
            //return _signer.RecoverPublicKey(discoveryMessage.Signature, Keccak.Compute(Bytes.Concat(new[] {(byte)discoveryMessage.MessageType}, discoveryMessage.Payload)));

            var key = _signer.RecoverPublicKey(new Signature(signature, recoveryId), Keccak.Compute(Bytes.Concat(messageType, data)));
            return key != null ? new NodeId(key) : null;
        }
    }
}