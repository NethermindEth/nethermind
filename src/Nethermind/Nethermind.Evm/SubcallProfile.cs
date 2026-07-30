// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>
/// Diagnostic census of depth-1 subcalls per top-level frame, gathered for the speculative
/// parallel-subcall design: how many sibling call sites a request has, and what share of them
/// revert (a reverted sibling has an empty write set, which is what makes it safely
/// parallelizable). Rides the opcode-histogram switch so it needs no plumbing of its own, and
/// the enabled flag is static readonly so a disabled build carries no cost in the frame loop.
/// Exceptionally failing children never reach the merge path and are not counted; the clean
/// REVERT pattern this exists to measure does.
/// </summary>
public static class SubcallProfile
{
    private static readonly string s_path = Environment.GetEnvironmentVariable("NETHERMIND_OPCODE_HISTOGRAM");
    public static readonly bool IsEnabled = !string.IsNullOrEmpty(s_path);

    private static readonly ConcurrentDictionary<AddressAsKey, (long Calls, long Reverts)> s_sites = new();
    private static long s_topFrames;
    private static long s_depth1Calls;
    private static long s_depth1Reverts;
    private static Timer s_timer;

    public static void RecordTopFrame()
    {
        if (Interlocked.Increment(ref s_topFrames) == 1)
        {
            s_timer = new Timer(static _ => Flush(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    public static void RecordSubcall(Address callee, bool reverted)
    {
        Interlocked.Increment(ref s_depth1Calls);
        if (reverted) Interlocked.Increment(ref s_depth1Reverts);
        s_sites.AddOrUpdate(callee, static (_, r) => (1, r ? 1L : 0L), static (_, prev, r) => (prev.Calls + 1, prev.Reverts + (r ? 1L : 0L)), reverted);
    }

    private static void Flush()
    {
        long frames = Interlocked.Read(ref s_topFrames);
        long calls = Interlocked.Read(ref s_depth1Calls);
        long reverts = Interlocked.Read(ref s_depth1Reverts);
        StringBuilder sb = new();
        sb.AppendLine($"# Depth-1 subcall census - top frames {frames:N0}, depth-1 calls {calls:N0} ({(frames == 0 ? 0 : (double)calls / frames):F1}/frame), reverted {reverts:N0} ({(calls == 0 ? 0 : 100.0 * reverts / calls):F1} %)");
        sb.AppendLine("# callee                                          calls      reverts");
        foreach ((AddressAsKey callee, (long c, long r)) in s_sites.ToArray().OrderByDescending(static kv => kv.Value.Calls).Take(30))
        {
            sb.AppendLine($"{callee.Value,-44} {c,10:N0} {r,10:N0}");
        }

        try
        {
            File.WriteAllText(s_path + ".subcalls", sb.ToString());
        }
        catch (IOException)
        {
        }
    }
}
