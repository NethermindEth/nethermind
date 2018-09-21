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
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Network.Crypto;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Network.Rlpx.Handshake
{
    /// <summary>
    ///     https://github.com/ethereum/devp2p/blob/master/rlpx.md
    /// </summary>
    public class EncryptionHandshakeService : IEncryptionHandshakeService
    {
        private static int MacBitsSize = 256;

        private readonly ICryptoRandom _cryptoRandom;
        private readonly IEciesCipher _eciesCipher;
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly PrivateKey _privateKey;
        private readonly ILogger _logger;
        private readonly ISigner _signer;

        public EncryptionHandshakeService(
            IMessageSerializationService messageSerializationService,
            IEciesCipher eciesCipher,
            ICryptoRandom cryptoRandom,
            ISigner signer,
            PrivateKey privateKey,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _messageSerializationService = messageSerializationService ?? throw new ArgumentNullException(nameof(messageSerializationService));
            _eciesCipher = eciesCipher ?? throw new ArgumentNullException(nameof(eciesCipher));
            _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));;
            _cryptoRandom = cryptoRandom ?? throw new ArgumentNullException(nameof(cryptoRandom));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        }

        public Packet Auth(NodeId remoteNodeId, EncryptionHandshake handshake)
        {
            handshake.RemoteNodeId = remoteNodeId;
            handshake.InitiatorNonce = _cryptoRandom.GenerateRandomBytes(32);
            handshake.EphemeralPrivateKey = new PrivateKeyProvider(_cryptoRandom).PrivateKey;

            byte[] staticSharedSecret = BouncyCrypto.Agree(_privateKey, remoteNodeId.PublicKey);
            byte[] forSigning = staticSharedSecret.Xor(handshake.InitiatorNonce);

            AuthEip8Message authMessage = new AuthEip8Message();
            authMessage.Nonce = handshake.InitiatorNonce;
            authMessage.PublicKey = _privateKey.PublicKey;
            authMessage.Signature = _signer.Sign(handshake.EphemeralPrivateKey, new Keccak(forSigning));

            byte[] authData = _messageSerializationService.Serialize(authMessage);
            int size = authData.Length + 32 + 16 + 65; // data + MAC + IV + pub
            byte[] sizeBytes = size.ToBigEndianByteArray().Slice(2, 2);
            byte[] packetData = _eciesCipher.Encrypt(
                remoteNodeId.PublicKey,
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
            try
            {
                if(_logger.IsTrace) _logger.Trace($"Trying to decrypt an old version of {nameof(AuthMessage)}");
                byte[] plaintextOld = _eciesCipher.Decrypt(_privateKey, auth.Data);
                authMessage = _messageSerializationService.Deserialize<AuthMessage>(plaintextOld);
                isOld = true;
            }
            catch (Exception)
            {
                if(_logger.IsTrace) _logger.Trace($"Trying to decrypt version 4 of {nameof(AuthEip8Message)}");
                byte[] sizeData = auth.Data.Slice(0, 2);
                byte[] plaintext = _eciesCipher.Decrypt(_privateKey, auth.Data.Slice(2), sizeData);
                authMessage = _messageSerializationService.Deserialize<AuthEip8Message>(plaintext);
            }

            var nodeId = new NodeId(authMessage.PublicKey);
            if(_logger.IsTrace) _logger.Trace($"Received AUTH v{authMessage.Version} from {nodeId}");

            handshake.RemoteNodeId = nodeId;
            handshake.RecipientNonce = _cryptoRandom.GenerateRandomBytes(32);
            handshake.EphemeralPrivateKey = new PrivateKeyProvider(_cryptoRandom).PrivateKey;

            handshake.InitiatorNonce = authMessage.Nonce;
            byte[] staticSharedSecret = BouncyCrypto.Agree(_privateKey, handshake.RemoteNodeId.PublicKey);
            byte[] forSigning = staticSharedSecret.Xor(handshake.InitiatorNonce);

            handshake.RemoteEphemeralPublicKey = _signer.RecoverPublicKey(authMessage.Signature, new Keccak(forSigning));

            byte[] ackData;
            if (isOld) // what was the difference? shall I really include ephemeral public key in v4?
            {
                if(_logger.IsTrace) _logger.Trace($"Building an {nameof(AckMessage)}");
                AckMessage ackMessage = new AckMessage();
                ackMessage.EphemeralPublicKey = handshake.EphemeralPrivateKey.PublicKey;
                ackMessage.Nonce = handshake.RecipientNonce;
                ackData = _messageSerializationService.Serialize(ackMessage);
            }
            else
            {
                if(_logger.IsTrace) _logger.Trace($"Building an {nameof(AckEip8Message)}");
                AckEip8Message ackMessage = new AckEip8Message();
                ackMessage.EphemeralPublicKey = handshake.EphemeralPrivateKey.PublicKey;
                ackMessage.Nonce = handshake.RecipientNonce;
                ackData = _messageSerializationService.Serialize(ackMessage);    
            }
            
            int size = ackData.Length + 32 + 16 + 65; // data + MAC + IV + pub
            byte[] sizeBytes = size.ToBigEndianByteArray().Slice(2, 2);
            byte[] packetData = _eciesCipher.Encrypt(handshake.RemoteNodeId.PublicKey, ackData, sizeBytes);
            handshake.AckPacket = new Packet(Bytes.Concat(sizeBytes, packetData));
            SetSecrets(handshake, HandshakeRole.Recipient);
            return handshake.AckPacket;
        }

        public void Agree(EncryptionHandshake handshake, Packet ack)
        {
            handshake.AckPacket = ack;

            try
            {
                byte[] plaintextOld = _eciesCipher.Decrypt(_privateKey, ack.Data);
                AckMessage ackMessage = _messageSerializationService.Deserialize<AckMessage>(plaintextOld);
                if(_logger.IsTrace) _logger.Trace($"Received ACK old");
                
                handshake.RemoteEphemeralPublicKey = ackMessage.EphemeralPublicKey;
                handshake.RecipientNonce = ackMessage.Nonce;
            }
            catch (Exception)
            {
                byte[] sizeData = ack.Data.Slice(0, 2);
                byte[] plaintext = _eciesCipher.Decrypt(_privateKey, ack.Data.Slice(2), sizeData);
                AckEip8Message ackMessage = _messageSerializationService.Deserialize<AckEip8Message>(plaintext);
                if(_logger.IsTrace) _logger.Trace($"Received ACK v{ackMessage.Version}");
                
                handshake.RemoteEphemeralPublicKey = ackMessage.EphemeralPublicKey;
                handshake.RecipientNonce = ackMessage.Nonce;
            }

            SetSecrets(handshake, HandshakeRole.Initiator);
        }

        private void SetSecrets(EncryptionHandshake handshake, HandshakeRole handshakeRole)
        {
            byte[] ephemeralSharedSecret = BouncyCrypto.Agree(handshake.EphemeralPrivateKey, handshake.RemoteEphemeralPublicKey);
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

            if(_logger.IsTrace) _logger.Trace($"Agreed secrets with {handshake.RemoteNodeId}");
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
    }
}