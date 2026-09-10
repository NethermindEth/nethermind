// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nethermind.Stateless.Execution;

namespace Nethermind.Stateless.Check;

/// <summary>
/// Runs every <c>&lt;id&gt;.in</c> of a corpus directory through the stateless executor on the host and
/// compares the output with <c>&lt;id&gt;.out</c>, the way <c>nm-stateless check</c> does for the Rust
/// executor. Prints the count matched and the wall-clock time, and the executor's split between
/// the Rust and the C# interpreter when the Rust one is linked in.
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: Nethermind.Stateless.Check <corpus dir> [repeat]");
            return 2;
        }

        string dir = args[0];
        int repeat = args.Length > 1 ? int.Parse(args[1]) : 1;
        string[] inputs = Directory.GetFiles(dir, "*.in").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        int matched = 0, failed = 0;
        Stopwatch clock = Stopwatch.StartNew();
        foreach (string input in inputs)
        {
            byte[] bytes = File.ReadAllBytes(input);
            byte[] expected = File.ReadAllBytes(Path.ChangeExtension(input, ".out"));
            byte[] actual = [];
            for (int i = 0; i < repeat; i++)
            {
                actual = StatelessExecutor.Execute(bytes).ToArray();
            }
            if (actual.AsSpan().SequenceEqual(expected))
            {
                matched++;
            }
            else
            {
                failed++;
                Console.WriteLine($"{Path.GetFileNameWithoutExtension(input)}: expected {Convert.ToHexStringLower(expected)}, got {Convert.ToHexStringLower(actual)}");
            }
        }
        clock.Stop();
        Console.WriteLine($"{matched} of {inputs.Length} cases matched in {clock.Elapsed.TotalSeconds:F1}s");
#if RUST_EVM
        Console.WriteLine($"frames: rust {Nethermind.Evm.Rust.RustVirtualMachine.RustFrames}, csharp {Nethermind.Evm.Rust.RustVirtualMachine.CSharpFrames}, rust evm available: {Nethermind.Evm.Rust.RustVirtualMachine.IsAvailable}, inside {Stopwatch.GetElapsedTime(0, Nethermind.Evm.Rust.RustVirtualMachine.InsideTicks).TotalSeconds:F1}s write-back {Stopwatch.GetElapsedTime(0, Nethermind.Evm.Rust.RustVirtualMachine.WriteBackTicks).TotalSeconds:F1}s");
#endif
        return failed == 0 ? 0 : 1;
    }
}
