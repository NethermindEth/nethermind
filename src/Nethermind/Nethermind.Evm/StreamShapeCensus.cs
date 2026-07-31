// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Nethermind.Evm;

/// <summary>
/// Diagnostic census of block-local dataflow in the stream interpreter, sizing a block-scoped
/// register machine before any of it is built: an "edge" is one stack read performed by an
/// in-block op, and the edge is "local" when the value it reads was pushed earlier in the same
/// block - exactly the reads a register allocator could keep out of stack memory. The analyzer
/// counts both per block at build time; the executor accumulates them per block charge, so the
/// totals are execution-weighted. Enabled by NETHERMIND_STREAM_CENSUS=&lt;report path&gt;; the
/// static flag makes the disabled branch vanish under JIT like the opcode histogram's.
/// Undercounts slightly: blocks entered mid-way (metered walks, peephole landings) never charge,
/// so their ops are not counted - acceptable for a sizing measurement.
/// </summary>
public static class StreamShapeCensus
{
    public static readonly bool IsEnabled;
    private static readonly string? s_path;
    private static readonly Timer? s_flushTimer;

    private static long s_ops;
    private static long s_edges;
    private static long s_localEdges;
    private static long s_blockCharges;

    static StreamShapeCensus()
    {
        s_path = Environment.GetEnvironmentVariable("NETHERMIND_STREAM_CENSUS");
        IsEnabled = !string.IsNullOrWhiteSpace(s_path);
        if (!IsEnabled)
        {
            return;
        }

        s_flushTimer = new Timer(static _ => Flush(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Flush();
    }

    public static void OnBlockCharge(int ops, int edges, int localEdges)
    {
        Interlocked.Add(ref s_ops, ops);
        Interlocked.Add(ref s_edges, edges);
        Interlocked.Add(ref s_localEdges, localEdges);
        Interlocked.Increment(ref s_blockCharges);
    }

    private static void Flush()
    {
        long ops = Interlocked.Read(ref s_ops);
        long edges = Interlocked.Read(ref s_edges);
        long local = Interlocked.Read(ref s_localEdges);
        long charges = Interlocked.Read(ref s_blockCharges);

        StringBuilder report = new();
        report.AppendLine("stream shape census (execution-weighted, charged blocks only)");
        report.AppendLine($"block charges:        {charges}");
        report.AppendLine($"precharged ops:       {ops}");
        report.AppendLine($"stack read edges:     {edges}");
        report.AppendLine($"block-local edges:    {local}");
        if (edges > 0)
        {
            report.AppendLine($"local share:          {100.0 * local / edges:F2}%");
        }

        if (charges > 0)
        {
            report.AppendLine($"ops per charge:       {(double)ops / charges:F2}");
            report.AppendLine($"local edges / charge: {(double)local / charges:F2}");
        }

        try
        {
            File.WriteAllText(s_path!, report.ToString());
        }
        catch (IOException)
        {
            // A torn write on shutdown loses one flush of a diagnostic file; never the process.
        }
    }
}
