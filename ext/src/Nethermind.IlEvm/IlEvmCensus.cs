// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.IlEvm;

/// <summary>
/// With <c>NETHERMIND_ILEVM_CENSUS=1</c>, compiled code counts how often each form of the memory, arithmetic and EXP
/// opcodes it emits runs, and of what operands: which sites take a helper, what their operands are at run time.
/// The counts are what a fast path would be worth. Each counted site costs a call, so it is not for timing runs.
/// </summary>
/// <remarks><c>NETHERMIND_ILEVM_CENSUS_OUT=&lt;file&gt;</c> writes <see cref="Describe"/> there at process exit.</remarks>
public static class IlEvmCensus
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_CENSUS") == "1";

    /// <summary>Run-time classes per counted form.</summary>
    internal const int Classes = 32;

    private const int MaxForms = 4096;

    private static readonly ConcurrentDictionary<string, int> _ids = new();
    private static readonly string[] _names = new string[MaxForms];
    private static readonly string[]?[] _classNames = new string[]?[MaxForms];
    private static readonly long[] _sites = new long[MaxForms];
    private static readonly long[] _counts = new long[MaxForms * Classes];
    private static int _forms;

    static IlEvmCensus()
    {
        string? output = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_CENSUS_OUT");
        if (Enabled && !string.IsNullOrEmpty(output))
            AppDomain.CurrentDomain.ProcessExit += (_, _) => System.IO.File.WriteAllLines(output, Describe());
    }

    /// <summary>The id of a counted form, registered on first use; each call counts one more emitted site of it.</summary>
    internal static int Site(string name, string[]? classNames = null)
    {
        int id = _ids.GetOrAdd(name, static (n, c) =>
        {
            int next = Interlocked.Increment(ref _forms) - 1;
            if (next >= MaxForms)
                return MaxForms - 1;
            _names[next] = n;
            _classNames[next] = c;
            return next;
        }, classNames);
        Interlocked.Increment(ref _sites[id]);
        return id;
    }

    public static void Reset()
    {
        Array.Clear(_counts);
        Array.Clear(_sites);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Hit(int id) => Interlocked.Increment(ref _counts[id * Classes]);

    internal static readonly string[] DivisorClasses =
        ["0", "1", "pow2", "u32", "u64", "u128", "u192", "wide", "0 x<y", "1 x<y", "pow2 x<y", "u32 x<y", "u64 x<y", "u128 x<y", "u192 x<y", "wide x<y",
         "pow2 x<2^64", "u32 x<2^64", "u64 x<2^64", "u128 x<2^128", "u192 x<2^192"];

    internal static readonly string[] WidthPairClasses =
        ["u64*u64", "u64*u128", "u64*u192", "u64*wide", "u128*u64", "u128*u128", "u128*u192", "u128*wide",
         "u192*u64", "u192*u128", "u192*u192", "u192*wide", "wide*u64", "wide*u128", "wide*u192", "wide*wide"];

    internal static readonly string[] MemoryClasses = ["offset>=2^32", "in place", "grows", "body"];

    internal static readonly string[] ExpClasses = BuildExpClasses();

    private static string[] BuildExpClasses()
    {
        string[] bases = ["0/1", "2", "10", "256", "u64", "wide", "-1"];
        string[] exponents = ["e=0", "e<256", "e<65536", "e wide"];
        List<string> names = [];
        foreach (string b in bases)
            foreach (string e in exponents)
                names.Add($"b={b} {e}");
        return [.. names];
    }

    /// <summary>A word's width class: 0 for u64, 1 for u128, 2 for u192, 3 wider.</summary>
    private static int Width(in UInt256 value) => value.u3 != 0 ? 3 : value.u2 != 0 ? 2 : value.u1 != 0 ? 1 : 0;

    /// <summary>The class of a divisor: 0, 1, a power of two, then by width.</summary>
    internal static int DivisorClass(in UInt256 d)
    {
        if (d.IsZero) return 0;
        if (d.IsOne) return 1;
        int ones = System.Numerics.BitOperations.PopCount(d.u0) + System.Numerics.BitOperations.PopCount(d.u1) +
                   System.Numerics.BitOperations.PopCount(d.u2) + System.Numerics.BitOperations.PopCount(d.u3);
        if (ones == 1) return 2;
        if (d.u3 != 0) return 7;
        if (d.u2 != 0) return 6;
        if (d.u1 != 0) return 5;
        return d.u0 <= uint.MaxValue ? 3 : 4;
    }

    /// <summary>A two-word DIV, MOD, SDIV or SMOD whose operands are in the slots at <paramref name="top"/> and below it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Divide(ref byte top, int id)
    {
        ref UInt256 a = ref Unsafe.As<byte, UInt256>(ref top);
        ref UInt256 b = ref Unsafe.Subtract(ref a, 1);
        Count(id, DivideClass(in a, in b));
    }

    /// <summary>The class of x / d: the divisor's, past 8 when x &lt; d (no division), past 16 when x is no wider than d's class bound.</summary>
    internal static int DivideClass(in UInt256 x, in UInt256 d)
    {
        int divisor = DivisorClass(in d);
        if (x < d) return 8 + divisor;
        int dividendWidth = Width(in x);
        return divisor switch
        {
            2 when dividendWidth == 0 => 16,
            3 when dividendWidth == 0 => 17,
            4 when dividendWidth == 0 => 18,
            5 when dividendWidth <= 1 => 19,
            6 when dividendWidth <= 2 => 20,
            _ => divisor,
        };
    }

    /// <summary>A DIV or MOD by a constant divisor, of the dividend in the slot at <paramref name="slot"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void DivideByConstant(ref byte slot, int id, ulong d0, ulong d1, ulong d2, ulong d3)
    {
        UInt256 d = new(d0, d1, d2, d3);
        Count(id, DivideClass(in Unsafe.As<byte, UInt256>(ref slot), in d));
    }

    /// <summary>A constant dividend over the divisor in the slot at <paramref name="slot"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ConstantByDivisor(ref byte slot, int id, ulong x0, ulong x1, ulong x2, ulong x3)
    {
        UInt256 x = new(x0, x1, x2, x3);
        Count(id, DivideClass(in x, in Unsafe.As<byte, UInt256>(ref slot)));
    }

    /// <summary>A two-word MUL, by its operands' widths.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Multiply(ref byte top, int id)
    {
        ref UInt256 a = ref Unsafe.As<byte, UInt256>(ref top);
        ref UInt256 b = ref Unsafe.Subtract(ref a, 1);
        Count(id, Width(in a) * 4 + Width(in b));
    }

    /// <summary>A word in a slot, by width: for the operand of a form whose other operand is a constant.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Operand(ref byte slot, int id) => Count(id, Width(in Unsafe.As<byte, UInt256>(ref slot)));

    /// <summary>EXP of the base in the slot below <paramref name="top"/>, to the exponent in it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Exp(ref byte top, int id)
    {
        ref UInt256 exponent = ref Unsafe.As<byte, UInt256>(ref top);
        ref UInt256 b = ref Unsafe.Subtract(ref exponent, 1);
        int baseClass = b.IsZero || b.IsOne ? 0
            : b == UInt256.MaxValue ? 6
            : b.u1 == 0 && b.u2 == 0 && b.u3 == 0 ? b.u0 switch { 2 => 1, 10 => 2, 256 => 3, _ => 4 }
            : 5;
        int exponentClass = exponent.IsZero ? 0 : !exponent.IsUint64 || exponent.u0 >= 65536 ? 3 : exponent.u0 >= 256 ? 2 : 1;
        Count(id, baseClass * 4 + exponentClass);
    }

    /// <summary>An MLOAD at the offset in the slot at <paramref name="slot"/>: in place, or through the body.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void MLoad(ref EvmPooledMemory memory, ref byte slot, int id)
    {
        ref UInt256 offset = ref Unsafe.As<byte, UInt256>(ref slot);
        if (!offset.IsUint64 || offset.u0 > uint.MaxValue)
        {
            Count(id, 0);
            return;
        }

        Count(id, Unsafe.IsNullRef(ref memory.GetActiveInitializedWord(offset.u0)) ? 3 : 1);
    }

    /// <summary>An MLOAD at a constant offset below 2^32.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void MLoadAt(ref EvmPooledMemory memory, ulong offset, int id) =>
        Count(id, Unsafe.IsNullRef(ref memory.GetActiveInitializedWord(offset)) ? 3 : 1);

    /// <summary>An MSTORE at the offset in the slot at <paramref name="slot"/>: in place, growing within the backing, or through the body.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void MStore(ref EvmPooledMemory memory, ref byte slot, int id)
    {
        ref UInt256 offset = ref Unsafe.As<byte, UInt256>(ref slot);
        if (!offset.IsUint64 || offset.u0 > uint.MaxValue)
        {
            Count(id, 0);
            return;
        }

        MStoreAt(ref memory, offset.u0, id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void MStoreAt(ref EvmPooledMemory memory, ulong offset, int id)
    {
        ulong cost = memory.GetWordOverwriteCost(offset);
        Count(id, cost == ulong.MaxValue ? 3 : !Unsafe.IsNullRef(ref memory.GetActiveInitializedWord(offset)) ? 1 : 2);
    }

    private static void Count(int id, int @class) => Interlocked.Increment(ref _counts[id * Classes + Math.Min(@class, Classes - 1)]);

    /// <summary>Every form that ran or was emitted: sites, runs, and runs by class; forms sorted by runs.</summary>
    public static IEnumerable<string> Describe()
    {
        int forms = Math.Min(Volatile.Read(ref _forms), MaxForms);
        List<(string Name, long Sites, long Runs, string Detail)> rows = [];
        long allRuns = 0;
        for (int id = 0; id < forms; id++)
        {
            long runs = 0;
            List<string> parts = [];
            for (int c = 0; c < Classes; c++)
            {
                long n = Volatile.Read(ref _counts[id * Classes + c]);
                if (n == 0) continue;
                runs += n;
                string[]? names = _classNames[id];
                parts.Add($"{(names is not null && c < names.Length ? names[c] : c.ToString())}={n}");
            }

            allRuns += runs;
            rows.Add((_names[id], Volatile.Read(ref _sites[id]), runs, parts.Count > 1 || _classNames[id] is not null ? string.Join(" ", parts) : ""));
        }

        yield return $"census: {forms} forms, {allRuns} counted runs";
        foreach ((string name, long sites, long runs, string detail) in rows.OrderByDescending(static r => r.Runs).ThenByDescending(static r => r.Sites))
            yield return $"{runs,14} {sites,8}  {name}{(detail.Length > 0 ? "  | " + detail : "")}";
    }
}
