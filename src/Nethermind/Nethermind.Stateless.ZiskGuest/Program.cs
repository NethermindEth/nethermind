// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    [UnmanagedCallersOnly(EntryPoint = "ZkvmThrow")]
    static unsafe void HandleException(void* exception)
    {
        if (!StatelessExecutor.Output.IsEmpty)
        {
            IO.WriteOutput(StatelessExecutor.Output.Span);

            // For debugging purposes
            IO.PrintLine(Convert.ToHexStringLower(StatelessExecutor.Output.Span));
        }

        nint ptr = (nint)exception;
        Exception ex = Unsafe.As<nint, Exception>(ref ptr);

        IO.PrintLine($"{ex.GetType().FullName}: {ex.Message}");

        Environment.Exit(1);
    }
}
