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
        byte[] input = Zisk.IO.ReadInput();

        Block block = StatelessExecutor.Execute(input);

        Span<byte> hash = block.Hash!.Bytes;
        var size = sizeof(uint);

        for (int i = 0, count = hash.Length / size; i < count; i++)
            Zisk.IO.SetOutput(i, BinaryPrimitives.ReadUInt32BigEndian(hash[(i * size)..]));

        return 0;
    }
}
