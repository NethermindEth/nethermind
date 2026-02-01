// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

public readonly struct Entry
{
    public ushort Type { get; }
    public ulong Length { get; }
    public Entry(ushort type, ulong length)
    {
        Length = length;
        Type = type;
    }
}

