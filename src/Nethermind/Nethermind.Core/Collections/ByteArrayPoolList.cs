// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace Nethermind.Core.Collections;

public class ByteArrayPoolList : ArrayPoolList<byte>
{
    public ByteArrayPoolList(int capacity) : base(capacity)
    {
    }

    public ByteArrayPoolList(int capacity, IEnumerable<byte> enumerable) : base(capacity, enumerable)
    {
    }

    public ByteArrayPoolList(ArrayPool<byte> arrayPool, int capacity) : base(arrayPool, capacity)
    {
    }

    public MemoryStream AsMemoryStream() => new(_array, 0, Count);
}
