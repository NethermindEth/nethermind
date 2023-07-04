// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Crypto
{
    public interface ICryptoRandom : IDisposable
    {
        byte[] GenerateRandomBytes(int length);
        void GenerateRandomBytes(Span<byte> bytes);
        int NextInt(int max);
    }
}
