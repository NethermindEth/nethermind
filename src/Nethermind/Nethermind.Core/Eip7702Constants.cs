// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using System;

namespace Nethermind.Core;
public static class Eip7702Constants
{
    private readonly static byte[] _delegationHeader = [0xef, 0x01, 0x00];
    public const byte Magic = 0x05;
    public static ReadOnlySpan<byte> DelegationHeader => _delegationHeader.AsSpan();

    public static readonly UInt256 DelegationDesignatorLength = 23;

    private static readonly int HeaderLength = DelegationHeader.Length;
    public static bool IsDelegatedCode(ReadOnlySpan<byte> code) =>
        code.Length == HeaderLength + Address.Size
        && DelegationHeader.SequenceEqual(code[..DelegationHeader.Length]);
}
