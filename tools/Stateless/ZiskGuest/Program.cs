// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Stateless.Execution;

namespace Nethermind.Stateless.ZiskGuest;

class Program
{
    static int Main()
    {
        ReadOnlySpan<byte> input = Zisk.IO.ReadInput();
        Block block = StatelessExecutor.Execute(input);

        Span<byte> hash = block.Hash!.Bytes;
        var size = sizeof(uint);

        for (int i = 0, j = 0; i < hash.Length; i += size)
            Zisk.IO.SetOutput(j++, BinaryPrimitives.ReadUInt32BigEndian(hash.Slice(i, size)));

        return 0;
    }
}
