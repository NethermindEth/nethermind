// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Stateless.Guest;

partial class Program
{
    private static partial void WriteOutput(ReadOnlySpan<byte> output)
    {
        // For debugging purposes
        IO.PrintLine(Convert.ToHexStringLower(output));

        IO.WriteOutput(output);
    }

    /// <remarks>
    /// ZisK's 256-bit modular arithmetic and division run on its arith256 precompiles, outside the shared
    /// zkvm_* interface, so SP1 and OpenVM have no counterpart and only this guest installs them. Each is one
    /// precompile call (division verifies a hinted quotient) against hundreds to about 1,800 steps for the
    /// software UInt256 routines. The hinted division stays sound: div_rem256_c checks that q·b + r equals a
    /// with a zero high word and that r is below b, so a prover cannot substitute another pair.
    /// </remarks>
    static partial void InstallAccelerators()
    {
        unsafe
        {
            Arith256Accelerators.Install(
                &Accelerators.AddMod256, &Accelerators.MulMod256, &Accelerators.ReduceMod256, &Accelerators.DivRem256);
        }
    }
}
