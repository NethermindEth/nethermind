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

        public IAuthMessage InitiateAuth(PrivateKey privateKey, string hostName, int port)
        {
            AuthMessage authMessage = new AuthMessage();
            authMessage.IsTokenUsed = false;
            authMessage.Nonce = _cryptoRandom.GenerateRandomBytes(AuthMessage.NonceLength);
            authMessage.PublicKey = privateKey.PublicKey;

//            authMessage.Signature = _signer.Sign();
//            authMessage.EphemeralPublicHash =
            return authMessage;
        }

        public IAuthResponseMessage RespondToAuth(PrivateKey privateKey, IAuthMessage authMessage)
        {
            throw new NotImplementedException(); // TODO: always respond with V4??? 
        }
    }
}