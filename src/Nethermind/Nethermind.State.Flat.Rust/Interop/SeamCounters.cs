// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>
/// The seam counters of the native library, for its cost to be told apart from the work behind it: the calls of every
/// export and, while timing is on, the time spent in each, and the reads of its snapshot bundles by where they were
/// served from. They are process-wide totals over every thread, published as <see cref="Metrics"/>.
/// </summary>
/// <remarks>
/// Timing costs two clock reads per call, so it is off unless the environment variable <see cref="TimingVariable"/>
/// is <c>1</c> when the first store is created.
/// </remarks>
internal static unsafe class SeamCounters
{
    public const string TimingVariable = "NETHERMIND_FLATDB_SEAM_TIMING";

    private static readonly int Count = Enum.GetValues<SeamCounter>().Length;
    private static readonly Lazy<string[]> _names = new(ReadNames);
    private static int _timingApplied;

    /// <summary>Turns timing on when <see cref="TimingVariable"/> asks for it; only the first call reads it.</summary>
    public static void ApplyTimingVariable()
    {
        if (Interlocked.Exchange(ref _timingApplied, 1) == 0 && Environment.GetEnvironmentVariable(TimingVariable) == "1")
        {
            NativeMethods.fdb_seam_timing(1);
        }
    }

    /// <summary>The name the library gives each counter, by <see cref="SeamCounter"/>.</summary>
    public static IReadOnlyList<string> Names => _names.Value;

    /// <summary>The calls, or reads, and nanoseconds of every counter so far, by <see cref="SeamCounter"/>.</summary>
    /// <exception cref="InvalidOperationException">The library has other counters than this adapter knows.</exception>
    public static (ulong[] Calls, ulong[] Nanoseconds) Read()
    {
        ulong[] calls = new ulong[Count];
        ulong[] nanoseconds = new ulong[Count];
        nuint reported;
        fixed (ulong* callsPointer = calls, nanosecondsPointer = nanoseconds)
        {
            reported = NativeMethods.fdb_seam_counters(callsPointer, nanosecondsPointer, (nuint)Count);
        }

        if (reported != (nuint)Count)
        {
            throw new InvalidOperationException($"The native flat DB library has {reported} seam counters, this adapter {Count}.");
        }

        return (calls, nanoseconds);
    }

    /// <summary>Copies the counters to <see cref="Metrics"/>.</summary>
    public static void Publish()
    {
        (ulong[] calls, ulong[] nanoseconds) = Read();
        IReadOnlyList<string> names = Names;
        for (int i = 0; i < calls.Length; i++)
        {
            Metrics.FlatRustSeamCalls[names[i]] = (long)calls[i];
            if (nanoseconds[i] != 0) Metrics.FlatRustSeamNanoseconds[names[i]] = (long)nanoseconds[i];
        }
    }

    private static string[] ReadNames()
    {
        string[] names = new string[Count];
        for (int i = 0; i < Count; i++)
        {
            byte* name;
            nuint length;
            StatusMap.ThrowIfFailed(NativeMethods.fdb_seam_counter_name((uint)i, &name, &length));
            names[i] = Encoding.ASCII.GetString(name, (int)length);
        }

        return names;
    }
}
