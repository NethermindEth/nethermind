using System;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;
using Nevermind.Discovery.Messages;
using Nevermind.Network;

namespace Nevermind.Discovery
{
    public class DiscoveryMessageSerializer<T> : IMessageSerializer<T> where T : DiscoveryMessage
    {
        private readonly PrivateKey _privateKey;
        private readonly ISigner _signer;

        public DiscoveryMessageSerializer(ISigner signer, PrivateKey privateKey)
        {
            _signer = signer;
            _privateKey = privateKey;
        }

        public byte[] Serialize(T message, IMessagePad pad = null)
        {
            byte[] typeBytes = {(byte)message.MessageType};
            Keccak toSign = Keccak.Compute(Bytes.Concat(typeBytes, message.Payload));
            Signature signature = _signer.Sign(_privateKey, toSign);
            message.Signature = signature;
            byte[] signatureBytes = Bytes.Concat(signature.Bytes, signature.RecoveryId);
            byte[] mdc = Keccak.Compute(Bytes.Concat(signatureBytes, typeBytes, message.Payload)).Bytes;
            return Bytes.Concat(mdc, signatureBytes, typeBytes, message.Payload);
        }

        public T Deserialize(byte[] bytes)
        {
            T message = Activator.CreateInstance<T>();
            message.Signature = new Signature(bytes.Slice(32, 64), bytes[96]);
            // TODO: needs to recognize by type bytes - need to be done outside
            message.Payload = bytes.Slice(98);
            if (!Bytes.UnsafeCompare(bytes.Slice(0, 32), Keccak.Compute(bytes.Slice(32)).Bytes))
            {
                throw new NetworkingException("Invalid MDC");
            }

            return message;
        }
    }
}