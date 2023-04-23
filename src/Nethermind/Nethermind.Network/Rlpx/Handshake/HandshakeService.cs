// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Secp256k1;
using Nethermind.Serialization.Rlp;


namespace Nethermind.Network.Rlpx.Handshake
{
    /// <summary>
    ///     https://github.com/ethereum/devp2p/blob/master/rlpx.md
    /// </summary>
    public class HandshakeService : IHandshakeService
    {
        private static int MacBitsSize = 256;
        private static int MacBytesSize = MacBitsSize / 8;

        private readonly IPrivateKeyGenerator _ephemeralGenerator;
        private readonly ICryptoRandom _cryptoRandom;
        private readonly IEciesCipher _eciesCipher;
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly PrivateKey _privateKey;
        private readonly ILogger _logger;
        private readonly IEcdsa _ecdsa;

        public HandshakeService(
            IMessageSerializationService messageSerializationService,
            IEciesCipher eciesCipher,
            ICryptoRandom cryptoRandom,
            IEcdsa ecdsa,
            PrivateKey privateKey,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<HandshakeService>() ?? throw new ArgumentNullException(nameof(logManager));
            _messageSerializationService = messageSerializationService ?? throw new ArgumentNullException(nameof(messageSerializationService));
            _eciesCipher = eciesCipher ?? throw new ArgumentNullException(nameof(eciesCipher));
            _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
            _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _ephemeralGenerator = new PrivateKeyGenerator(_cryptoRandom);
        }

        public Packet Auth(PublicKey remoteNodeId, EncryptionHandshake handshake, bool preEip8Format = false)
        {
            handshake.RemoteNodeId = remoteNodeId;
            handshake.InitiatorNonce = _cryptoRandom.GenerateRandomBytes(32);
            handshake.EphemeralPrivateKey = _ephemeralGenerator.Generate();

            byte[] staticSharedSecret = Proxy.EcdhSerialized(remoteNodeId.Bytes, _privateKey.KeyBytes);
            byte[] forSigning = staticSharedSecret.Xor(handshake.InitiatorNonce);

            if (preEip8Format)
            {
                AuthMessage authMessage = new()
                {
                    Nonce = handshake.InitiatorNonce,
                    PublicKey = _privateKey.PublicKey,
                    Signature = _ecdsa.Sign(handshake.EphemeralPrivateKey, new Keccak(forSigning)),
                    IsTokenUsed = false,
                    EphemeralPublicHash = Keccak.Compute(handshake.EphemeralPrivateKey.PublicKey.Bytes)
                };

                IByteBuffer authData = _messageSerializationService.ZeroSerialize(authMessage);
                try
                {
                    byte[] packetData = _eciesCipher.Encrypt(remoteNodeId, authData.ReadAllBytesAsArray(), Array.Empty<byte>());
                    handshake.AuthPacket = new Packet(packetData);
                    return handshake.AuthPacket;
                }
                finally
                {
                    authData.SafeRelease();
                }

            }
            else
            {
                AuthEip8Message authMessage = new()
                {
                    Nonce = handshake.InitiatorNonce,
                    PublicKey = _privateKey.PublicKey,
                    Signature = _ecdsa.Sign(handshake.EphemeralPrivateKey, new Keccak(forSigning))
                };

                IByteBuffer authData = _messageSerializationService.ZeroSerialize(authMessage);
                try
                {
                    int size = authData.ReadableBytes + 32 + 16 + 65; // data + MAC + IV + pub
                    byte[] sizeBytes = size.ToBigEndianByteArray().Slice(2, 2);
                    byte[] packetData = _eciesCipher.Encrypt(remoteNodeId, authData.ReadAllBytesAsArray(), sizeBytes);
                    handshake.AuthPacket = new Packet(Bytes.Concat(sizeBytes, packetData));
                    return handshake.AuthPacket;
                }
                finally
                {
                    authData.SafeRelease();
                }

            }
        }

        public Packet Ack(EncryptionHandshake handshake, Packet auth)
        {
            handshake.AuthPacket = auth;

            AuthMessageBase authMessage;
            bool preEip8Format = false;
            byte[] plainText = null;
            try
            {
                if (_logger.IsTrace) _logger.Trace($"Trying to decrypt an old version of {nameof(AuthMessage)}");
                (preEip8Format, plainText) = _eciesCipher.Decrypt(_privateKey, auth.Data);
            }
            catch (Exception ex)
            {
                if (_logger.IsTrace) _logger.Trace($"Exception when decrypting ack {ex.Message}");
            }

            if (preEip8Format)
            {
                authMessage = _messageSerializationService.Deserialize<AuthMessage>(plainText);
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Trying to decrypt version 4 of {nameof(AuthEip8Message)}");
                byte[] sizeData = auth.Data.Slice(0, 2);
                (_, plainText) = _eciesCipher.Decrypt(_privateKey, auth.Data.Slice(2), sizeData);
                authMessage = _messageSerializationService.Deserialize<AuthEip8Message>(plainText);
            }

            PublicKey nodeId = authMessage.PublicKey;
            if (_logger.IsTrace) _logger.Trace($"Received AUTH v{authMessage.Version} from {nodeId}");

            handshake.RemoteNodeId = nodeId;
            handshake.RecipientNonce = _cryptoRandom.GenerateRandomBytes(32);
            handshake.EphemeralPrivateKey = _ephemeralGenerator.Generate();

            handshake.InitiatorNonce = authMessage.Nonce;
            byte[] staticSharedSecret = Proxy.EcdhSerialized(handshake.RemoteNodeId.Bytes, _privateKey.KeyBytes);
            byte[] forSigning = staticSharedSecret.Xor(handshake.InitiatorNonce);

            handshake.RemoteEphemeralPublicKey = _ecdsa.RecoverPublicKey(authMessage.Signature, new Keccak(forSigning));

            byte[] data;
            if (preEip8Format)
            {
                if (_logger.IsTrace) _logger.Trace($"Building an {nameof(AckMessage)}");
                AckMessage ackMessage = new()
                {
                    EphemeralPublicKey = handshake.EphemeralPrivateKey.PublicKey,
                    Nonce = handshake.RecipientNonce
                };

                IByteBuffer ackData = _messageSerializationService.ZeroSerialize(ackMessage);
                try
                {
                    data = _eciesCipher.Encrypt(handshake.RemoteNodeId, ackData.ReadAllBytesAsArray(), Array.Empty<byte>());
                }
                finally
                {
                    ackData.SafeRelease();
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Building an {nameof(AckEip8Message)}");
                AckEip8Message ackMessage = new()
                {
                    EphemeralPublicKey = handshake.EphemeralPrivateKey.PublicKey,
                    Nonce = handshake.RecipientNonce
                };
                IByteBuffer ackData = _messageSerializationService.ZeroSerialize(ackMessage);
                try
                {
                    int size = ackData.ReadableBytes + 32 + 16 + 65; // data + MAC + IV + pub
                    byte[] sizeBytes = size.ToBigEndianByteArray().Slice(2, 2);
                    data = Bytes.Concat(sizeBytes, _eciesCipher.Encrypt(handshake.RemoteNodeId, ackData.ReadAllBytesAsArray(), sizeBytes));
                }
                finally
                {
                    ackData.SafeRelease();
                }
            }

            handshake.AckPacket = new Packet(data);
            SetSecrets(handshake, HandshakeRole.Recipient);
            return handshake.AckPacket;
        }

        public void Agree(EncryptionHandshake handshake, Packet ack)
        {
            handshake.AckPacket = ack;

            bool preEip8Format = false;
            byte[] plainText = null;
            try
            {
                (preEip8Format, plainText) = _eciesCipher.Decrypt(_privateKey, ack.Data);
            }
            catch (Exception ex)
            {
                if (_logger.IsTrace) _logger.Trace($"Exception when decrypting agree {ex.Message}");
            }

            if (preEip8Format)
            {
                AckMessage ackMessage = _messageSerializationService.Deserialize<AckMessage>(plainText);
                if (_logger.IsTrace) _logger.Trace("Received ACK old");

                handshake.RemoteEphemeralPublicKey = ackMessage.EphemeralPublicKey;
                handshake.RecipientNonce = ackMessage.Nonce;
            }
            else
            {
                byte[] sizeData = ack.Data.Slice(0, 2);
                (_, plainText) = _eciesCipher.Decrypt(_privateKey, ack.Data.Slice(2), sizeData);

                AckEip8Message ackEip8Message = _messageSerializationService.Deserialize<AckEip8Message>(plainText);
                if (_logger.IsTrace) _logger.Trace($"Received ACK v{ackEip8Message.Version}");

                handshake.RemoteEphemeralPublicKey = ackEip8Message.EphemeralPublicKey;
                handshake.RecipientNonce = ackEip8Message.Nonce;
            }

            SetSecrets(handshake, HandshakeRole.Initiator);

            if (_logger.IsTrace) _logger.Trace($"Agreed secrets with {handshake.RemoteNodeId}");
#if DEBUG
            if (_logger.IsTrace)
            {
                _logger.Trace($"{handshake.RemoteNodeId} ephemeral private key {handshake.EphemeralPrivateKey}");
                _logger.Trace($"{handshake.RemoteNodeId} initiator nonce {handshake.InitiatorNonce.ToHexString()}");
                _logger.Trace($"{handshake.RemoteNodeId} recipient nonce {handshake.RecipientNonce.ToHexString()}");
                _logger.Trace($"{handshake.RemoteNodeId} remote ephemeral public key {handshake.RemoteEphemeralPublicKey}");
                _logger.Trace($"{handshake.RemoteNodeId} remote public key {handshake.RemoteNodeId}");
                _logger.Trace($"{handshake.RemoteNodeId} auth packet {handshake.AuthPacket.Data.ToHexString()}");
                _logger.Trace($"{handshake.RemoteNodeId} ack packet {handshake.AckPacket.Data.ToHexString()}");
            }
#endif
        }

        public static void SetSecrets(EncryptionHandshake handshake, HandshakeRole handshakeRole)
        {
            Span<byte> tempConcat = stackalloc byte[64];
            Span<byte> ephemeralSharedSecret = Proxy.EcdhSerialized(handshake.RemoteEphemeralPublicKey.Bytes, handshake.EphemeralPrivateKey.KeyBytes);
            Span<byte> nonceHash = ValueKeccak.Compute(Bytes.Concat(handshake.RecipientNonce, handshake.InitiatorNonce)).BytesAsSpan;
            ephemeralSharedSecret.CopyTo(tempConcat[..32]);
            nonceHash.CopyTo(tempConcat.Slice(32, 32));
            Span<byte> sharedSecret = ValueKeccak.Compute(tempConcat).BytesAsSpan;
            //            byte[] token = Keccak.Compute(sharedSecret).Bytes;
            sharedSecret.CopyTo(tempConcat.Slice(32, 32));
            byte[] aesSecret = Keccak.Compute(tempConcat).Bytes;

            sharedSecret.Clear();
            aesSecret.CopyTo(tempConcat.Slice(32, 32));
            byte[] macSecret = Keccak.Compute(tempConcat).Bytes;

            ephemeralSharedSecret.Clear();
            handshake.Secrets = new EncryptionSecrets();
            //            handshake.Secrets.Token = token;
            handshake.Secrets.AesSecret = aesSecret;
            handshake.Secrets.MacSecret = macSecret;

            KeccakHash mac1 = KeccakHash.Create(MacBytesSize);
            mac1.Update(macSecret.Xor(handshake.RecipientNonce));
            mac1.Update(handshake.AuthPacket.Data);

            KeccakHash mac2 = KeccakHash.Create(MacBytesSize);
            mac2.Update(macSecret.Xor(handshake.InitiatorNonce));
            mac2.Update(handshake.AckPacket.Data);

            if (handshakeRole == HandshakeRole.Initiator)
            {
                handshake.Secrets.EgressMac = mac1;
                handshake.Secrets.IngressMac = mac2;
            }
            else
            {
                handshake.Secrets.EgressMac = mac2;
                handshake.Secrets.IngressMac = mac1;
            }
        }
    }
}
