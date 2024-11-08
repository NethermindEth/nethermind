// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using System;

namespace Nethermind.Core;
public static class Eip7702Constants
{
    private readonly static byte[] _delegationHeader = [0xef, 0x01, 0x00];
    public const byte Magic = 0x05;
    public static ReadOnlySpan<byte> DelegationHeader => _delegationHeader.AsSpan();
    /// <summary>
    /// Any code reading operation will only act on the first two bytes of the header
    /// </summary>
    public static ReadOnlyMemory<byte> FirstTwoBytesOfHeader => _delegationHeader.AsMemory(0, 2);

    private static readonly int HeaderLength = DelegationHeader.Length;
    public static bool IsDelegatedCode(ReadOnlySpan<byte> code) =>
        code.Length == HeaderLength + Address.Size
        && DelegationHeader.SequenceEqual(code.Slice(0, DelegationHeader.Length));

    public static readonly Hash256 HashOfDelegationCode = Keccak.Compute(FirstTwoBytesOfHeader.Span);
}
