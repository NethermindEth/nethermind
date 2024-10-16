// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

internal static class EntryTypes
{
    public const UInt16 Version = 0x3265;
    public const UInt16 CompressedHeader = 0x03;
    public const UInt16 CompressedBody = 0x04;
    public const UInt16 CompressedReceipts = 0x05;
    public const UInt16 TotalDifficulty = 0x06;
    public const UInt16 Accumulator = 0x07;
    public const UInt16 BlockIndex = 0x3266;
}
