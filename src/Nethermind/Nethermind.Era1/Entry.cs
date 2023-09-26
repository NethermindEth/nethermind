// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

internal class Entry
{
    public ushort Type;
    public byte[] Value;
    public Entry(ushort t) : this(t, Array.Empty<byte>())
    {}
    public Entry(ushort t, byte[] v)
    {
        Type = t;
        Value = v;
    }
    public StreamArray ValueAsStream()
    {
        return new StreamArray(Value);
    }

}

