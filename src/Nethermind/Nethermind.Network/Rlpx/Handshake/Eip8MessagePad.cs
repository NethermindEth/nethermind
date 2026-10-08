// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Crypto;

namespace Nethermind.Network.Rlpx.Handshake
{
    public class Eip8MessagePad(ICryptoRandom cryptoRandom) : IMessagePad
    {
        readonly ICryptoRandom _cryptoRandom = cryptoRandom;

        public int GetPaddingLength() => 100 + _cryptoRandom.NextInt(201);

        public void Pad(Span<byte> padding) => _cryptoRandom.GenerateRandomBytes(padding);
    }
}
