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
    // Statically-linked zisk libc exit (--wrap=exit -> a7=93 ecall). The native
    // exit (not Environment.Exit) must be used to terminate the handler: while an
    // exception is in flight, Environment.Exit re-enters the throw path and
    // recurses through this handler forever.
    [DllImport("*", EntryPoint = "exit")]
    static extern void NativeExit(int code);

    // Re-entrancy guard: anything we touch in the handler below (Message,
    // GetType().ToString(), ...) may itself throw, which re-enters this hook.
    // Without the guard that recurses forever and eventually corrupts memory.
    static bool s_handling;

    // Under --libc zisk a managed `throw` is lowered to RhpThrowEx, which the
    // bflat image redirects via --wrap=RhpThrowEx -> __wrap_RhpThrowEx, forwarding
    // the exception object (a0) to this weak hook. Without an exported ZkvmThrow
    // the guest fail-fasts silently before writing output; here we print the
    // exception so decoding/validation failures are visible, then exit.
    [UnmanagedCallersOnly(EntryPoint = "ZkvmThrow")]
    static void ZkvmThrow(IntPtr exceptionObj)
    {
        // A throw raised while already handling means one of the prints below
        // re-threw; bail out cleanly instead of recursing.
        if (s_handling)
            NativeExit(2);
        s_handling = true;

        IO.PrintLine("[ZkvmThrow] Unhandled exception in Nethermind ZisK guest:");

        // The a0 pointer value IS the managed object reference; reinterpret it.
        Exception ex = Unsafe.As<IntPtr, Exception>(ref exceptionObj);

        // Message first (most likely available without reflection metadata).
        IO.PrintLine(ex.Message);

        // The managed StackTrace is empty under the zisk fail-fast throw model, so
        // reconstruct the call stack natively: scan our stack frame for words that
        // look like return addresses into .text and dump them. Map offline with
        // `nm` to recover the faulting method and its callers.
        DumpReturnAddresses();

        NativeExit(1);
    }

    // .text address range of the guest ELF (statically linked, fixed layout).
    const ulong TextLow = 0x8008_0000UL;
    const ulong TextHigh = 0x80A6_0000UL;

    static unsafe void DumpReturnAddresses()
    {
        byte* probe = stackalloc byte[8];
        nuint sp = (nuint)probe;
        int printed = 0;

        // Stack grows down: return addresses pushed by callers sit above `sp`.
        for (nuint p = sp; p < sp + 16384 && printed < 80; p += 8)
        {
            ulong v = *(ulong*)p;
            if (v >= TextLow && v < TextHigh)
            {
                PrintHex("RA ", v);
                printed++;
            }
        }
    }

    static void PrintHex(string label, ulong v)
    {
        // Manual hex (no globalization / formatting that could throw).
        Span<char> buf = stackalloc char[18];
        buf[0] = '0';
        buf[1] = 'x';
        for (int i = 0; i < 16; i++)
        {
            int nib = (int)((v >> ((15 - i) * 4)) & 0xF);
            buf[2 + i] = (char)(nib < 10 ? '0' + nib : 'a' + nib - 10);
        }
        IO.PrintLine(label + new string(buf));
    }

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
