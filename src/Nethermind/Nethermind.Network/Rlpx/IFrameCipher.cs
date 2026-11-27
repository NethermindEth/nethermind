// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.Rlpx
{
    public interface IFrameCipher
    {
        /// <remarks><paramref name="output"/> may be the same memory as <paramref name="input"/>.</remarks>
        void Encrypt(ReadOnlySpan<byte> input, Span<byte> output);

        /// <remarks><paramref name="output"/> may be the same memory as <paramref name="input"/>.</remarks>
        void Decrypt(ReadOnlySpan<byte> input, Span<byte> output);
    }
}
