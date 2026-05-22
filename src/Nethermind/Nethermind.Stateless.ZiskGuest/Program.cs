// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Stateless.Execution;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Stateless.ZiskGuest;

class Program
{
    static int Main()
    {
        ReadOnlySpan<byte> input = IO.ReadInput();
        ReadOnlySpan<byte> output = StatelessExecutor.Execute(input);

        IO.WriteOutput(output);

        // For debugging purposes
        IO.PrintLine(Convert.ToHexStringLower(output));

        return 0;
    }
}
