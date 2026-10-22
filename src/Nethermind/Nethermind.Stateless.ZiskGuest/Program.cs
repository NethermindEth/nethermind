// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
}
