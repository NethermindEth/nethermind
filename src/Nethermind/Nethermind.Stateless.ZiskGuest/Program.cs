// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Stateless.Execution;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Stateless.ZiskGuest;

class Program
{
    static int Main()
    {
        ReadOnlySpan<byte> input = IO.ReadInput();

        Block block = StatelessExecutor.Execute(input);

        // TODO: Output zkEVM standard format when ready
        IO.WriteOutput(block.Hash!.Bytes);

        // TODO: Remove when zkEVM standard output format is ready
        IO.PrintLine(block.Hash.ToString());

        return 0;
    }
}
