// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Stateless.Execution;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Stateless.OpenVmGuest;

class Program
{
    /// <summary>
    /// OpenVM's public output is a fixed 32-byte window, and the validation
    /// result is 43 bytes (SSZ: a 32-byte root, a flag, a chain id and a schema
    /// id), so this guest publishes the Keccak-256 of the result rather than the
    /// result itself. That is not a choice this guest can make differently:
    /// <c>DEFAULT_MAX_NUM_PUBLIC_VALUES</c> is 32 and <c>cargo openvm keygen</c>
    /// refuses any other value, so a larger window means a proving key off the
    /// supported path. Writing past the window would panic rather than truncate.
    ///
    /// THE VERIFIER MUST HASH TOO. Where the ZisK and SP1 guests publish the
    /// result bytes for the verifier to compare directly, here the verifier has
    /// to encode the result it expects and compare Keccak-256 digests. The hash
    /// is one accelerated instruction on OpenVM, so it costs essentially
    /// nothing; the asymmetry in the contract is what to be careful about.
    /// </summary>
    const int DigestLength = 32;

    static int Main()
    {
        ReadOnlySpan<byte> input = IO.ReadInput();
        ReadOnlySpan<byte> output = StatelessExecutor.Execute(input);

        WriteOutput(output);

        return 0;
    }

    static void WriteOutput(ReadOnlySpan<byte> output)
    {
        // The full result, for debugging: only the digest is provable, so this
        // is the only place the bytes behind it are visible at all.
        IO.PrintLine(Convert.ToHexStringLower(output));

        Span<byte> digest = stackalloc byte[DigestLength];

        Accelerators.Keccak256(output, digest);

        IO.WriteOutput(digest);
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
