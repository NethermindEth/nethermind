// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Snappier;

namespace Nethermind.Era1;

internal struct Entry
{
    public ushort Type { get; }
    public long Offset { get; }
    public long ValueOffset => Offset + E2Store.HeaderSize;
    public long Length { get; }
    public Entry(ushort type, long offset, long length)
    {
        Length = length;
        Offset = offset;
        Type = type;
    }
}

