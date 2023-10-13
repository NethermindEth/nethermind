// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

internal static class EntryTypes
{
    public const UInt16 TypeVersion = 0x3265;
    public const UInt16 TypeCompressedHeader = 0x03;
    public const UInt16 TypeCompressedBody = 0x04;
    public const UInt16 TypeCompressedReceipts = 0x05;
    public const UInt16 TypeTotalDifficulty = 0x06;
    public const UInt16 TypeAccumulator = 0x07;
    public const UInt16 TypeBlockIndex = 0x3266;
    public const UInt16 MaxEra1Size = 8192;
}
