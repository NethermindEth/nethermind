// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>The three outcomes this task's reporting rules require - never collapsed into one another.</summary>
public enum VectorOutcome
{
    Pass,
    Fail,
    NotImplemented,
}

/// <summary>One vector's result, keyed by suite/fork/preset for the aggregate report.</summary>
public readonly record struct VectorRecord(string Suite, string Fork, string Preset, string VectorName, VectorOutcome Outcome, string? Detail);

/// <summary>
/// Collects every vector outcome across the whole assembly run and prints one aggregate report - the
/// per suite/fork/preset pass/fail/not-implemented counts, plus the full failure and not-implemented
/// listings - required by this task's reporting rules. A per-vector NUnit test result (pass, fail, or
/// <see cref="Assert.Inconclusive(string)"/> for not-implemented) still carries the vector's own name for
/// per-vector debugging; this is the cross-cutting summary on top of that.
/// </summary>
public static class ConsensusSpecTestSummary
{
    private static readonly ConcurrentBag<VectorRecord> Records = [];

    public static void Record(string suite, string fork, string preset, string vectorName, VectorOutcome outcome, string? detail = null) =>
        Records.Add(new VectorRecord(suite, fork, preset, vectorName, outcome, detail));

    /// <summary>Runs <paramref name="body"/>, records its outcome, and reproduces the pass/fail/inconclusive result on the calling test.</summary>
    public static void RunAndRecord(string suite, string fork, string preset, string vectorName, Action body)
    {
        try
        {
            body();
            Record(suite, fork, preset, vectorName, VectorOutcome.Pass);
        }
        catch (NotImplementedInDriverException notImplemented)
        {
            Record(suite, fork, preset, vectorName, VectorOutcome.NotImplemented, notImplemented.Message);
            Assert.Inconclusive(notImplemented.Message);
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            Record(suite, fork, preset, vectorName, VectorOutcome.Fail, ex.ToString());
            throw;
        }
        catch (AssertionException ex)
        {
            Record(suite, fork, preset, vectorName, VectorOutcome.Fail, ex.Message);
            throw;
        }
    }

    public static void PrintReport()
    {
        List<VectorRecord> all = [.. Records];
        if (all.Count == 0)
        {
            TestContext.Progress.WriteLine("[consensus-spec-tests] no vectors were run.");
            return;
        }

        StringBuilder sb = new();
        sb.AppendLine();
        sb.AppendLine("==================== consensus spec test report ====================");

        foreach (IGrouping<(string Suite, string Preset, string Fork), VectorRecord> group in all
                     .GroupBy(r => (r.Suite, r.Preset, r.Fork))
                     .OrderBy(g => g.Key.Suite, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Preset, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Fork, StringComparer.Ordinal))
        {
            int pass = group.Count(r => r.Outcome == VectorOutcome.Pass);
            int fail = group.Count(r => r.Outcome == VectorOutcome.Fail);
            int notImpl = group.Count(r => r.Outcome == VectorOutcome.NotImplemented);
            sb.AppendLine($"{group.Key.Suite,-16} {group.Key.Preset,-8} {group.Key.Fork,-10} passed={pass,-5} failed={fail,-5} not_implemented={notImpl,-5}");
        }

        sb.AppendLine();
        sb.AppendLine("---- failures ----");
        foreach (VectorRecord r in all.Where(r => r.Outcome == VectorOutcome.Fail).OrderBy(r => r.VectorName, StringComparer.Ordinal))
        {
            sb.AppendLine($"FAIL  [{r.Suite}/{r.Preset}/{r.Fork}] {r.VectorName}");
            if (r.Detail is not null)
                sb.AppendLine($"      {r.Detail.Replace("\n", "\n      ")}");
        }

        sb.AppendLine();
        sb.AppendLine("---- not implemented ----");
        foreach (VectorRecord r in all.Where(r => r.Outcome == VectorOutcome.NotImplemented).OrderBy(r => r.VectorName, StringComparer.Ordinal))
        {
            sb.AppendLine($"N/I   [{r.Suite}/{r.Preset}/{r.Fork}] {r.VectorName}{(r.Detail is null ? "" : $" - {r.Detail}")}");
        }

        sb.AppendLine("======================================================================");

        string report = sb.ToString();
        Console.WriteLine(report);
        try
        {
            // Durability net: this OneTimeTearDown's Console/TestContext output is not always
            // captured by the Microsoft.Testing.Platform NUnit adapter the same way a console
            // runner would, so the report is also written to a fixed file next to the test
            // assembly - never rely on this being the only place it lands, but never lose it either.
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "consensus-spec-report.txt"), report);
        }
        catch (IOException)
        {
            // Best-effort only; the console line above is the primary channel.
        }
    }
}

/// <summary>
/// Thrown by a suite driver when a vector names an operation, fork or preset this driver has no entry
/// point for. Caught by <see cref="ConsensusSpecTestSummary.RunAndRecord"/> and turned into
/// <see cref="Assert.Inconclusive(string)"/> plus a counted not-implemented record - never a silent skip
/// and never a pass.
/// </summary>
public sealed class NotImplementedInDriverException(string message) : Exception(message);
