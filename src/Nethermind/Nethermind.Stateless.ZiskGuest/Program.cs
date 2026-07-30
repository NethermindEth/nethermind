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

        WriteOutput(output);

        return 0;
    }

    static void WriteOutput(ReadOnlySpan<byte> output)
    {
        // For debugging purposes
        IO.PrintLine(Convert.ToHexStringLower(output));

        IO.WriteOutput(output);
    }

    static bool _handlingException;

    [UnmanagedCallersOnly(EntryPoint = "ZkvmThrow")]
    static unsafe void HandleException(void* exception)
    {
        if (_handlingException || StatelessExecutor.FailureOutput.IsEmpty)
            Environment.Exit(1);

        _handlingException = true;

        if (exception is null)
        {
            IO.PrintLine("An unknown error occurred.");
        }
        else
        {
            // SAFETY: a non-null `exception` is guaranteed by the runtime
            // to point to a valid managed exception object.
            nint ptr = (nint)exception;
            Exception ex = Unsafe.As<nint, Exception>(ref ptr);

            IO.PrintLine($"{ex.GetType().FullName}: {ex.Message}");
        }

        WriteOutput(StatelessExecutor.FailureOutput.Span);

        Environment.Exit(0);
    }
}
