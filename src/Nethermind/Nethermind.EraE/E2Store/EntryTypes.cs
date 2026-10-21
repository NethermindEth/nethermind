// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE.E2Store;

internal static class EntryTypes
{
    // Shared with era1
    public const ushort Version = 0x3265;
    public const ushort CompressedHeader = 0x03;
    public const ushort CompressedBody = 0x04;
    public const ushort TotalDifficulty = 0x06;
    public const ushort AccumulatorRoot = 0x07;

    // EraE-specific
    public const ushort CompressedSlimReceipts = 0x0a;
    public const ushort Proof = 0x0b;
    public const ushort ComponentIndex = 0x3267;
}
