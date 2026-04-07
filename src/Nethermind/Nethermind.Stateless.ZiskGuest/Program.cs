// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Stateless.Execution;
using Nethermind.ZiskBindings;

namespace Nethermind.Stateless.ZiskGuest;

class Program
{
    static int Main()
    {
        ReadOnlySpan<byte> input = IO.ReadInput();

        Block block = StatelessExecutor.Execute(input);
        Span<byte> hash = block.Hash!.Bytes;

        // TODO: Output zkEVM standard format when ready
        for (int i = 0, j = 0; i < hash.Length; i += sizeof(uint))
            IO.SetOutput(j++, BinaryPrimitives.ReadUInt32BigEndian(hash.Slice(i, sizeof(uint))));

        // TODO: Remove when zkEVM standard output format is ready
        IO.WriteLine(block.Hash.ToString());

        return 0;
    }
}
