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
    public class EncryptionHandshakeService : IEncryptionHandshakeService
    {
        private readonly ICryptoRandom _cryptoRandom;
        private readonly ISigner _signer;

        public EncryptionHandshakeService(ICryptoRandom cryptoRandom, ISigner signer)
        {
            _cryptoRandom = cryptoRandom;
            _signer = signer;
        }

        public AuthV4Message InitV4(EncryptionHandshake handshake, PrivateKey privateKey)
        {
            byte[] nonce = _cryptoRandom.GenerateRandomBytes(32);
            PrivateKey ephemeraPrivateKey = new PrivateKey(_cryptoRandom.GenerateRandomBytes(32));

            handshake.InitiatorNonce = nonce;
            handshake.EphemeralKey = ephemeraPrivateKey;

            byte[] secret = new byte[32]; // TODO: token used?
            byte[] forSigning = secret.Xor(nonce);

            AuthV4Message authV4Message = new AuthV4Message();
            authV4Message.Nonce = nonce;
            authV4Message.PublicKey = privateKey.PublicKey;
            authV4Message.Signature = _signer.Sign(privateKey, Keccak.Compute(forSigning));
            return authV4Message;
        }

        public AuthResponseMessage Respond(EncryptionHandshake handshake, AuthMessage authMessage)
        {
            handshake.InitiatorNonce = authMessage.Nonce;
            handshake.RemotePublicKey = authMessage.PublicKey;

            PrivateKey ephemeraPrivateKey = new PrivateKey(_cryptoRandom.GenerateRandomBytes(32));
            handshake.EphemeralKey = ephemeraPrivateKey;
            
            byte[] nonce = _cryptoRandom.GenerateRandomBytes(32);
            handshake.ResponderNonce = nonce;
            
            byte[] secret = new byte[32]; // TODO:
            byte[] forSigning = secret.Xor(handshake.InitiatorNonce);
            handshake.RemoteEphemeralKey = _signer.RecoverPublicKey(authMessage.Signature, Keccak.Compute(forSigning));            
            
            AuthResponseMessage responseMessage = new AuthResponseMessage();
            responseMessage.EphemeralPublicKey = handshake.RemoteEphemeralKey;
            responseMessage.IsTokenUsed = authMessage.IsTokenUsed;
            responseMessage.Nonce = nonce;
            return responseMessage;
        }

        public AuthResponseV4Message Respond(EncryptionHandshake handshake, AuthV4Message authMessage)
        {
            // TODO: DRY, V4 looks same as previously
            handshake.InitiatorNonce = authMessage.Nonce;
            handshake.RemotePublicKey = authMessage.PublicKey;

            PrivateKey ephemeraPrivateKey = new PrivateKey(_cryptoRandom.GenerateRandomBytes(32));
            handshake.EphemeralKey = ephemeraPrivateKey;
            
            byte[] nonce = _cryptoRandom.GenerateRandomBytes(32);
            handshake.ResponderNonce = nonce;
            
            byte[] secret = new byte[32]; // TODO:
            byte[] forSigning = secret.Xor(handshake.InitiatorNonce);
            handshake.RemoteEphemeralKey = _signer.RecoverPublicKey(authMessage.Signature, Keccak.Compute(forSigning));            
            
            AuthResponseV4Message responseMessage = new AuthResponseV4Message();
            responseMessage.EphemeralPublicKey = handshake.RemoteEphemeralKey;            
            responseMessage.Nonce = nonce;
            return responseMessage;
        }

        public void HandleResponse(EncryptionHandshake handshake, AuthResponseMessage responseMessage)
        {
            handshake.RemoteEphemeralKey = responseMessage.EphemeralPublicKey;
            handshake.ResponderNonce = responseMessage.Nonce;
            // TODO: calculate all secrets
        }

        public void HandleResponse(EncryptionHandshake handshake, AuthResponseV4Message responseMessage)
        {
            // TODO: DRY
            handshake.RemoteEphemeralKey = responseMessage.EphemeralPublicKey;
            handshake.ResponderNonce = responseMessage.Nonce;
            // TODO: calculate all secrets
        }

        public AuthMessage Init(EncryptionHandshake handshake, PrivateKey privateKey)
        {
            byte[] nonce = _cryptoRandom.GenerateRandomBytes(32);
            PrivateKey ephemeraPrivateKey = new PrivateKey(_cryptoRandom.GenerateRandomBytes(32));

            handshake.InitiatorNonce = nonce;
            handshake.EphemeralKey = ephemeraPrivateKey;

            byte[] secret = new byte[32]; // TODO:
            byte[] forSigning = secret.Xor(nonce);

            AuthMessage authMessage = new AuthMessage();
            authMessage.IsTokenUsed = false;
            authMessage.Nonce = nonce;
            authMessage.PublicKey = privateKey.PublicKey;
            authMessage.Signature = _signer.Sign(privateKey, Keccak.Compute(forSigning));
            authMessage.EphemeralPublicHash = Keccak.Compute(ephemeraPrivateKey.PublicKey.PrefixedBytes.Slice(1, 64));
            return authMessage;
        }
    }
}