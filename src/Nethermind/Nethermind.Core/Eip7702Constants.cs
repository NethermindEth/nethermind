// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;
public static class Eip7702Constants
{
    public const byte Magic = 0x05;
    public static ReadOnlySpan<byte> DelegationHeader => [0xef, 0x01, 0x00];
    public static bool IsDelegatedCode(ReadOnlySpan<byte> code) =>
        code.Length == DelegationHeader.Length + Address.Size
        && DelegationHeader.SequenceEqual(code.Slice(0, DelegationHeader.Length));
}
