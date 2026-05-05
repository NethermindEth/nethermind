// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

public readonly struct Entry(ushort type, ulong length)
{
    public ushort Type { get; } = type;
    public ulong Length { get; } = length;
}

