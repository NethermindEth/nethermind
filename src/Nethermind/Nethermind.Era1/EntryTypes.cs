// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

internal static class EntryTypes
{
    public const ushort Version = 0x3265;
    public const ushort CompressedHeader = 0x03;
    public const ushort CompressedBody = 0x04;
    public const ushort CompressedReceipts = 0x05;
    public const ushort TotalDifficulty = 0x06;
    public const ushort Accumulator = 0x07;
    public const ushort BlockIndex = 0x3266;
}
