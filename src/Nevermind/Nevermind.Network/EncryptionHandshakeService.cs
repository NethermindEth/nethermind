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

        public AuthV4Message Init(EncryptionHandshake handshake)
        {
            throw new NotImplementedException();
        }

        public AuthResponseMessage Respond(EncryptionHandshake handshake, AuthMessage authMessage)
        {
            throw new NotImplementedException();
        }

        public AuthResponseV4Message Respond(EncryptionHandshake handshake, AuthV4Message authMessage)
        {
            throw new NotImplementedException();
        }

        public void HandleResponse(EncryptionHandshake handshake, AuthResponseMessage responseMessage)
        {
            throw new NotImplementedException();
        }

        public void HandleResponse(EncryptionHandshake handshake, AuthResponseV4Message responseMessage)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        ///     EIP-8
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        private byte[] AddEip8Padding(byte[] message)
        {
            byte[] padding = _cryptoRandom.GenerateRandomBytes(100 + _cryptoRandom.NextInt(201));
            return Bytes.Concat(message, padding);
        }
    }
}