// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Stateless.Execution;

namespace Nethermind.Stateless.ZiskGuest;

class Program
{
    static void Main()
    {
        ReadOnlySpan<byte> input = Zisk.IO.ReadInput();

        if (!StatelessExecutor.TryExecute(input, out Block? block))
            Environment.FailFast("Execution failed");

        Span<byte> hash = block!.Hash!.Bytes;

        // TODO: Output chain id and state root too
        for (int i = 0, j = 0; i < hash.Length; i += sizeof(uint))
            Zisk.IO.SetOutput(j++, BinaryPrimitives.ReadUInt32BigEndian(hash.Slice(i, sizeof(uint))));
    }
}
