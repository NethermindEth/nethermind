// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Stateless.Execution;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Stateless.Guest;

/// <summary>The entry point and failure protocol every zkVM guest shares.</summary>
/// <remarks>
/// Compiled into each guest rather than referenced from a library: it is the
/// guest's <c>Main</c>, and <see cref="HandleException"/> carries an
/// <see cref="UnmanagedCallersOnlyAttribute"/> export the runtime looks up by
/// name in the guest assembly itself. Each guest supplies the one thing that
/// genuinely differs, <see cref="WriteOutput"/>, as the other half of this
/// partial class - ZisK and SP1 publish the result bytes, OpenVM the Keccak-256
/// of them.
/// </remarks>
partial class Program
{
    static int Main()
    {
        ReadOnlySpan<byte> input = IO.ReadInput();
        ReadOnlySpan<byte> output = StatelessExecutor.Execute(input);

        WriteOutput(output);

        return 0;
    }

    /// <summary>Publishes the validation result as this guest's proven output.</summary>
    /// <remarks>Implemented per target; see the guest's own Program.cs.</remarks>
    private static partial void WriteOutput(ReadOnlySpan<byte> output);

    static bool _handlingException;

    /// <summary>Last-resort handler for an exception escaping managed code.</summary>
    /// <remarks>
    /// Exits 0 after publishing <see cref="StatelessExecutor.FailureOutput"/>:
    /// a guest that failed validation still has a result to prove, and the
    /// distinction lives in the output rather than in the exit code. The
    /// <see cref="_handlingException"/> guard covers a second exception raised
    /// while reporting the first, which would otherwise recurse.
    /// </remarks>
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
