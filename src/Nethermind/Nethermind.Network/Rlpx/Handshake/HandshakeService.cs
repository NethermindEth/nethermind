/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Crypto;
using Nethermind.Secp256k1;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Network.Rlpx.Handshake
{
    /// <summary>
    ///     https://github.com/ethereum/devp2p/blob/master/rlpx.md
    /// </summary>
    public class HandshakeService : IHandshakeService
    {
        private static int MacBitsSize = 256;

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
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _messageSerializationService = messageSerializationService ?? throw new ArgumentNullException(nameof(messageSerializationService));
            _eciesCipher = eciesCipher ?? throw new ArgumentNullException(nameof(eciesCipher));
            _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
            ;
            _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _ephemeralGenerator = new PrivateKeyGenerator(_cryptoRandom);
        }

        public Packet Auth(PublicKey remoteNodeId, EncryptionHandshake handshake)
        {
            handshake.RemoteNodeId = remoteNodeId;
            handshake.InitiatorNonce = _cryptoRandom.GenerateRandomBytes(32);
            handshake.EphemeralPrivateKey = _ephemeralGenerator.Generate();

            byte[] staticSharedSecret = Proxy.EcdhSerialized(remoteNodeId.Bytes, _privateKey.KeyBytes);
            byte[] forSigning = staticSharedSecret.Xor(handshake.InitiatorNonce);

            AuthEip8Message authMessage = new AuthEip8Message();
            authMessage.Nonce = handshake.InitiatorNonce;
            authMessage.PublicKey = _privateKey.PublicKey;
            authMessage.Signature = _ecdsa.Sign(handshake.EphemeralPrivateKey, new Keccak(forSigning));

            byte[] authData = _messageSerializationService.Serialize(authMessage);
            int size = authData.Length + 32 + 16 + 65; // data + MAC + IV + pub
            byte[] sizeBytes = size.ToBigEndianByteArray().Slice(2, 2);
            byte[] packetData = _eciesCipher.Encrypt(
                remoteNodeId,
                authData,
                sizeBytes);

            handshake.AuthPacket = new Packet(Bytes.Concat(sizeBytes, packetData));
            return handshake.AuthPacket;
        }

        public Packet Ack(EncryptionHandshake handshake, Packet auth)
        {
            handshake.AuthPacket = auth;

            AuthMessageBase authMessage;
            bool isOld = false;
            byte[] plainText = null;
            try
            {
                if (_logger.IsTrace) _logger.Trace($"Trying to decrypt an old version of {nameof(AuthMessage)}");
                (isOld, plainText) = _eciesCipher.Decrypt(_privateKey, auth.Data);
            }
            catch (Exception ex)
            {
                if (_logger.IsTrace) _logger.Trace($"Exception when decrypting ack {ex.Message}");
            }

            if (!isOld)
            {
                if (_logger.IsTrace) _logger.Trace($"Trying to decrypt version 4 of {nameof(AuthEip8Message)}");
                byte[] sizeData = auth.Data.Slice(0, 2);
                (_, plainText) = _eciesCipher.Decrypt(_privateKey, auth.Data.Slice(2), sizeData);
                authMessage = _messageSerializationService.Deserialize<AuthEip8Message>(plainText);
            }
            else
            {
                authMessage = _messageSerializationService.Deserialize<AuthMessage>(plainText);
            }

            var nodeId = authMessage.PublicKey;
            if (_logger.IsTrace) _logger.Trace($"Received AUTH v{authMessage.Version} from {nodeId}");

            handshake.RemoteNodeId = nodeId;
            handshake.RecipientNonce = _cryptoRandom.GenerateRandomBytes(32);
            handshake.EphemeralPrivateKey = _ephemeralGenerator.Generate();

            handshake.InitiatorNonce = authMessage.Nonce;
            byte[] staticSharedSecret = Proxy.EcdhSerialized(handshake.RemoteNodeId.Bytes, _privateKey.KeyBytes);
            byte[] forSigning = staticSharedSecret.Xor(handshake.InitiatorNonce);

            handshake.RemoteEphemeralPublicKey = _ecdsa.RecoverPublicKey(authMessage.Signature, new Keccak(forSigning));

            byte[] ackData;
            if (isOld) // what was the difference? shall I really include ephemeral public key in v4?
            {
                if (_logger.IsTrace) _logger.Trace($"Building an {nameof(AckMessage)}");
                AckMessage ackMessage = new AckMessage();
                ackMessage.EphemeralPublicKey = handshake.EphemeralPrivateKey.PublicKey;
                ackMessage.Nonce = handshake.RecipientNonce;
                ackData = _messageSerializationService.Serialize(ackMessage);
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Building an {nameof(AckEip8Message)}");
                AckEip8Message ackMessage = new AckEip8Message();
                ackMessage.EphemeralPublicKey = handshake.EphemeralPrivateKey.PublicKey;
                ackMessage.Nonce = handshake.RecipientNonce;
                ackData = _messageSerializationService.Serialize(ackMessage);
            }

            int size = ackData.Length + 32 + 16 + 65; // data + MAC + IV + pub
            byte[] sizeBytes = size.ToBigEndianByteArray().Slice(2, 2);
            byte[] packetData = _eciesCipher.Encrypt(handshake.RemoteNodeId, ackData, sizeBytes);
            handshake.AckPacket = new Packet(Bytes.Concat(sizeBytes, packetData));
            SetSecrets(handshake, HandshakeRole.Recipient);
            return handshake.AckPacket;
        }

        public void Agree(EncryptionHandshake handshake, Packet ack)
        {
            handshake.AckPacket = ack;

            bool isOld = false;
            byte[] plainText = null;
            try
            {
                (isOld, plainText) = _eciesCipher.Decrypt(_privateKey, ack.Data);
            }
            catch (Exception ex)
            {
                if (_logger.IsTrace) _logger.Trace($"Exception when decrypting agree {ex.Message}");
            }

            if (isOld)
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
            byte[] ephemeralSharedSecret = Proxy.EcdhSerialized(handshake.RemoteEphemeralPublicKey.Bytes, handshake.EphemeralPrivateKey.KeyBytes);
            byte[] nonceHash = Keccak.Compute(Bytes.Concat(handshake.RecipientNonce, handshake.InitiatorNonce)).Bytes;
            byte[] sharedSecret = Keccak.Compute(Bytes.Concat(ephemeralSharedSecret, nonceHash)).Bytes;
            byte[] token = Keccak.Compute(sharedSecret).Bytes;
            byte[] aesSecret = Keccak.Compute(Bytes.Concat(ephemeralSharedSecret, sharedSecret)).Bytes;
            Array.Clear(sharedSecret, 0, sharedSecret.Length); // TODO: it was passed in the concat for Keccak so not good enough
            byte[] macSecret = Keccak.Compute(Bytes.Concat(ephemeralSharedSecret, aesSecret)).Bytes;
            Array.Clear(ephemeralSharedSecret, 0, ephemeralSharedSecret.Length); // TODO: it was passed in the concat for Keccak so not good enough
            handshake.Secrets = new EncryptionSecrets();
            handshake.Secrets.Token = token;
            handshake.Secrets.AesSecret = aesSecret;
            handshake.Secrets.MacSecret = macSecret;

            KeccakDigest mac1 = new KeccakDigest(MacBitsSize);
            mac1.BlockUpdate(macSecret.Xor(handshake.RecipientNonce), 0, macSecret.Length);
            mac1.BlockUpdate(handshake.AuthPacket.Data, 0, handshake.AuthPacket.Data.Length);

            KeccakDigest mac2 = new KeccakDigest(MacBitsSize);
            mac2.BlockUpdate(macSecret.Xor(handshake.InitiatorNonce), 0, macSecret.Length);
            mac2.BlockUpdate(handshake.AckPacket.Data, 0, handshake.AckPacket.Data.Length);

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