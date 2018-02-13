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

        public PublicKey GetNodeId(Message message)
        {
            return _signer.RecoverPublicKey(message.Signature, Keccak.Compute(Bytes.Concat(message.Type, message.Payload)));
        }
    }
}