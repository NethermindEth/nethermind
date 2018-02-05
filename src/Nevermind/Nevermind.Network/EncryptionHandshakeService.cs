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

using System.Diagnostics;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;
using ISigner = Nevermind.Core.Crypto.ISigner;

namespace Nevermind.Network
{
    /// <summary>
    /// https://github.com/ethereum/devp2p/blob/master/rlpx.md
    /// </summary>
    public class EncryptionHandshakeService : IEncryptionHandshakeService
    {
        // TODO: DRY with V4 here, review after sync finished
        
        private readonly ICryptoRandom _cryptoRandom;
        private readonly ISigner _signer;

        public EncryptionHandshakeService(ICryptoRandom cryptoRandom, ISigner signer)
        {
            _cryptoRandom = cryptoRandom;
            _signer = signer;
        }

        public AuthEip8Message Init(EncryptionHandshake handshake, PrivateKey privateKey)
        {
            Debug.Assert(handshake.RemotePublicKey != null, $"{nameof(handshake.RemotePublicKey)} has to be known");
            
            PrivateKey ephemeralPrivateKey = new PrivateKey(_cryptoRandom.GenerateRandomBytes(32));
            byte[] nonce = _cryptoRandom.GenerateRandomBytes(32);
            
            handshake.EphemeralPrivateKey = ephemeralPrivateKey;
            handshake.InitiatorNonce = nonce;

            byte[] staticSharedSecret = BouncyCrypto.Agree(privateKey, handshake.RemotePublicKey);
            byte[] forSigning = staticSharedSecret.Xor(nonce);

            AuthEip8Message authEip8Message = new AuthEip8Message();
            authEip8Message.Nonce = nonce;
            authEip8Message.PublicKey = privateKey.PublicKey;
            authEip8Message.Signature = _signer.Sign(ephemeralPrivateKey, Keccak.Compute(forSigning));
            return authEip8Message;
        }
        
        public AuthResponseMessage Respond(EncryptionHandshake handshake, AuthMessage authMessage, PrivateKey privateKey)
        {
            handshake.RemotePublicKey = authMessage.PublicKey;
            handshake.InitiatorNonce = authMessage.Nonce;

            PrivateKey ephemeraPrivateKey = new PrivateKey(_cryptoRandom.GenerateRandomBytes(32));
            handshake.EphemeralPrivateKey = ephemeraPrivateKey;

            byte[] nonce = _cryptoRandom.GenerateRandomBytes(32);
            handshake.ResponderNonce = nonce;

            byte[] staticSharedSecret = BouncyCrypto.Agree(privateKey, handshake.RemotePublicKey);
            byte[] forSigning = staticSharedSecret.Xor(handshake.InitiatorNonce);
            handshake.RemoteEphemeralPublicKey = _signer.RecoverPublicKey(authMessage.Signature, Keccak.Compute(forSigning));

            SetSecrets(handshake);
            
            AuthResponseMessage responseMessage = new AuthResponseMessage();
            responseMessage.EphemeralPublicKey = handshake.RemoteEphemeralPublicKey;
            responseMessage.IsTokenUsed = authMessage.IsTokenUsed;
            responseMessage.Nonce = nonce;
            return responseMessage;
        }

        public AuthResponseEip8Message Respond(EncryptionHandshake handshake, AuthEip8Message authMessage, PrivateKey privateKey)
        {
            // TODO: DRY, V4 looks same as previously
            handshake.RemotePublicKey = authMessage.PublicKey;
            handshake.InitiatorNonce = authMessage.Nonce;

            PrivateKey ephemeraPrivateKey = new PrivateKey(_cryptoRandom.GenerateRandomBytes(32));
            handshake.EphemeralPrivateKey = ephemeraPrivateKey;

            byte[] nonce = _cryptoRandom.GenerateRandomBytes(32);
            handshake.ResponderNonce = nonce;

            byte[] staticSharedSecret = BouncyCrypto.Agree(privateKey, handshake.RemotePublicKey);
            byte[] forSigning = staticSharedSecret.Xor(handshake.InitiatorNonce);
            handshake.RemoteEphemeralPublicKey = _signer.RecoverPublicKey(authMessage.Signature, Keccak.Compute(forSigning));

            SetSecrets(handshake);
            
            AuthResponseEip8Message responseMessage = new AuthResponseEip8Message();
            responseMessage.EphemeralPublicKey = ephemeraPrivateKey.PublicKey;
            responseMessage.Nonce = nonce;
            return responseMessage;
        }

        public void HandleResponse(EncryptionHandshake handshake, AuthResponseMessage responseMessage)
        {
            handshake.RemoteEphemeralPublicKey = responseMessage.EphemeralPublicKey;
            handshake.ResponderNonce = responseMessage.Nonce;
            
            SetSecrets(handshake);
        }

        public void HandleResponse(EncryptionHandshake handshake, AuthResponseEip8Message responseMessage)
        {
            // TODO: DRY
            handshake.RemoteEphemeralPublicKey = responseMessage.EphemeralPublicKey;
            handshake.ResponderNonce = responseMessage.Nonce;

            SetSecrets(handshake);
        }

        private static void SetSecrets(EncryptionHandshake handshake)
        {
            // TODO: destroy shared / ephemeral
            byte[] ephemeralSharedSecret = BouncyCrypto.Agree(handshake.EphemeralPrivateKey, handshake.RemoteEphemeralPublicKey);
            byte[] nonceHash = Keccak.Compute(Bytes.Concat(handshake.ResponderNonce, handshake.InitiatorNonce)).Bytes;
            byte[] sharedSecret = Keccak.Compute(Bytes.Concat(ephemeralSharedSecret, nonceHash)).Bytes;
            byte[] token = Keccak.Compute(sharedSecret).Bytes;
            byte[] aesSecret = Keccak.Compute(Bytes.Concat(ephemeralSharedSecret, sharedSecret)).Bytes;
            byte[] macSecret = Keccak.Compute(Bytes.Concat(ephemeralSharedSecret, aesSecret)).Bytes;
            handshake.Secrets = new EncryptionSecrets();
            handshake.Secrets.Token = token;
            handshake.Secrets.AesSecret = aesSecret;
            handshake.Secrets.MacSecret = macSecret;
        }

//        public AuthMessage Init(EncryptionHandshake handshake, PrivateKey privateKey)
//        {
//            PrivateKey ephemeraPrivateKey = new PrivateKey(_cryptoRandom.GenerateRandomBytes(32));
//            byte[] nonce = _cryptoRandom.GenerateRandomBytes(32);
//
//            handshake.InitiatorNonce = nonce;
//            handshake.EphemeralPrivateKey = ephemeraPrivateKey;
//
//            byte[] staticSharedSecret = BouncyCrypto.Agree(privateKey, handshake.RemotePublicKey);
//            byte[] forSigning = staticSharedSecret.Xor(nonce);
//
//            AuthMessage authMessage = new AuthMessage();
//            authMessage.IsTokenUsed = false;
//            authMessage.Nonce = nonce;
//            authMessage.PublicKey = privateKey.PublicKey;
//            authMessage.Signature = _signer.Sign(privateKey, Keccak.Compute(forSigning));
//            authMessage.EphemeralPublicHash = Keccak.Compute(ephemeraPrivateKey.PublicKey.Bytes);
//            return authMessage;
//        }
    }
}