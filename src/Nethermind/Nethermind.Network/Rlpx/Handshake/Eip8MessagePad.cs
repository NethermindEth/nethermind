// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Crypto;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class Eip8MessagePad : IMessagePad
    {
        readonly ICryptoRandom _cryptoRandom;

        public Eip8MessagePad(ICryptoRandom cryptoRandom)
        {
            _cryptoRandom = cryptoRandom;
        }

        public byte[] Pad(byte[] message)
        {
            byte[] padding = _cryptoRandom.GenerateRandomBytes(100 + _cryptoRandom.NextInt(201));
            return Bytes.Concat(message, padding);
        }
    }
}
