// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.FluentRlp;

internal static class RlpPrefix
{
    public const byte ShortString = 0x80;
    public const byte LongString = 0xB7;
    public const byte ShortList = 0xC0;
    public const byte LongList = 0xF7;
    public const int MaxShortLength = 55;
}
