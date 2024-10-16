// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Snappier;

namespace Nethermind.Era1;

internal readonly struct Entry
{
    public ushort Type { get; }
    public long Length { get; }
    public Entry(ushort type, long length)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Cannot be negativrae.");
        Length = length;
        Type = type;
    }
}

