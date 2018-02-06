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
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Network
{
    /// <summary>
    ///     https://github.com/ethereum/devp2p/blob/master/rlpx.md
    /// </summary>
    public class EncryptionHandshakeService : IEncryptionHandshakeService
    {
        // TODO: DRY with V4 here, review after sync finished

        private readonly ICryptoRandom _cryptoRandom;
        private readonly IEciesCipher _eciesCipher;
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly PrivateKey _privateKey;
        private readonly ISigner _signer;

        public EncryptionHandshakeService(
            IMessageSerializationService messageSerializationService,
            IEciesCipher eciesCipher,
            ICryptoRandom cryptoRandom,
            ISigner signer,
            PrivateKey privateKey)
        {
            _messageSerializationService = messageSerializationService;
            _eciesCipher = eciesCipher;
            _privateKey = privateKey;
            _cryptoRandom = cryptoRandom;
            _signer = signer;
        }

        public Packet Auth(PublicKey remoteNodePublicKey, EncryptionHandshake handshake)
        {
            handshake.RemotePublicKey = remoteNodePublicKey;

            byte[] nonce = _cryptoRandom.GenerateRandomBytes(32);

            handshake.EphemeralPrivateKey = new PrivateKey(_cryptoRandom.GenerateRandomBytes(32));
            handshake.InitiatorNonce = nonce;

            byte[] staticSharedSecret = BouncyCrypto.Agree(_privateKey, remoteNodePublicKey);
            byte[] forSigning = staticSharedSecret.Xor(nonce);

            AuthEip8Message authMessage = new AuthEip8Message();
            authMessage.Nonce = nonce;
            authMessage.PublicKey = _privateKey.PublicKey;
            authMessage.Signature = _signer.Sign(handshake.EphemeralPrivateKey, Keccak.Compute(forSigning));

            byte[] authData = _messageSerializationService.Serialize(authMessage);
            int size = authData.Length + 32 + 16 + 65; // data + MAC + IV + pub
            byte[] sizeBytes = size.ToBigEndianByteArray().Slice(2, 2);
            byte[] packetData = _eciesCipher.Encrypt(
                remoteNodePublicKey,
                authData,
                sizeBytes);

            handshake.AuthPacket = new Packet(Bytes.Concat(sizeBytes, packetData));    
            return handshake.AuthPacket;
        }

        public Packet Ack(EncryptionHandshake handshake, Packet auth)
        {
            handshake.AuthPacket = auth;
            
            // handle MAC here
            // try, retry
            byte[] sizeData = auth.Data.Slice(0, 2);
            byte[] plaintext = _eciesCipher.Decrypt(_privateKey, auth.Data.Slice(2), sizeData);
            AuthMessageBase authMessage = _messageSerializationService.Deserialize<AuthEip8Message>(plaintext);

            handshake.RemotePublicKey = authMessage.PublicKey;
            handshake.EphemeralPrivateKey = new PrivateKey(_cryptoRandom.GenerateRandomBytes(32));
            handshake.RecipientNonce = _cryptoRandom.GenerateRandomBytes(32);

            byte[] staticSharedSecret = BouncyCrypto.Agree(_privateKey, handshake.RemotePublicKey);
            byte[] forSigning = staticSharedSecret.Xor(handshake.InitiatorNonce);
            handshake.RemoteEphemeralPublicKey = _signer.RecoverPublicKey(authMessage.Signature, Keccak.Compute(forSigning));
            handshake.InitiatorNonce = authMessage.Nonce;

            // respond depending on the auth type
            AckEip8Message ackMessage = new AckEip8Message();
            ackMessage.EphemeralPublicKey = handshake.EphemeralPrivateKey.PublicKey;
//            responseMessage.IsTokenUsed = false; // TODO: need ot handle old clients with possible true values?
            ackMessage.Nonce = handshake.RecipientNonce;

            byte[] ackData = _messageSerializationService.Serialize(ackMessage);
            byte[] packetData = _eciesCipher.Encrypt(handshake.RemotePublicKey, ackData, null); // TODO: MAC
            handshake.AckPacket = new Packet(packetData);
            SetSecrets(handshake, Role.Recipient);
            return handshake.AckPacket;
        }

        public void Agree(EncryptionHandshake handshake, Packet ack)
        {
            handshake.AckPacket = ack;

            // try
            byte[] plaintext = _eciesCipher.Decrypt(_privateKey, ack.Data, null);
            AckEip8Message ackMessage = _messageSerializationService.Deserialize<AckEip8Message>(plaintext);

            handshake.RemoteEphemeralPublicKey = ackMessage.EphemeralPublicKey;
            handshake.RecipientNonce = ackMessage.Nonce;

            SetSecrets(handshake, Role.Initiator);
        }

        private static void SetSecrets(EncryptionHandshake handshake, Role role)
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
            handshake.Secrets.EgressMac = Keccak.Compute(
                Bytes.Concat(
                    macSecret.Xor(role == Role.Initiator ? handshake.RecipientNonce : handshake.InitiatorNonce),
                    role == Role.Initiator ? handshake.AuthPacket.Data : handshake.AckPacket.Data)).Bytes;
            handshake.Secrets.IngressMac = Keccak.Compute(
                Bytes.Concat(
                    macSecret.Xor(role == Role.Initiator ? handshake.InitiatorNonce : handshake.RecipientNonce),
                    role == Role.Initiator ? handshake.AckPacket.Data : handshake.AuthPacket.Data)).Bytes;
        }

        private enum Role
        {
            Initiator,
            Recipient
        }
    }
}