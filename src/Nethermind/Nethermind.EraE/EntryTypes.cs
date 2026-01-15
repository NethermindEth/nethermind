// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE;

internal static class EntryTypes
{
    public const ushort Version = 0x3265;
    public const ushort CompressedHeader = 0x03;
    public const ushort CompressedBody = 0x04;
    public const ushort CompressedSlimReceipts = 0x08;
    public const ushort TotalDifficulty = 0x06;
    public const ushort Proof = 0x09;
    public const ushort Accumulator = 0x07;
    public const ushort BlockIndex = 0x3267;
}
