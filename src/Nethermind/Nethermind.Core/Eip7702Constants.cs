// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using System;

namespace Nethermind.Core;

// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-7702.md
public static class Eip7702Constants
{
    private readonly static byte[] _delegationHeader = [0xef, 0x01, 0x00];
    public const byte Magic = 0x05;
    public static ReadOnlySpan<byte> DelegationHeader => _delegationHeader.AsSpan();

    public static readonly UInt256 DelegationDesignatorLength = 23;

    /// <summary> Gas cost to process one authorization tuple and set the delegation destination. </summary>
    public const long PerAuthBaseCost = 12_500;

    private static readonly int HeaderLength = DelegationHeader.Length;
    public static bool IsDelegatedCode(ReadOnlySpan<byte> code) =>
        code.Length == HeaderLength + Address.Size
        && DelegationHeader.SequenceEqual(code[..DelegationHeader.Length]);
}
