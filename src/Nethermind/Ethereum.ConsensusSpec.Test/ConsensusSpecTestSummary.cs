// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Ethereum.ConsensusSpec.Test;

public enum VectorOutcome
{
    Pass,
    Fail,
    NotImplemented,
}

public readonly record struct VectorRecord(string Suite, string Fork, string Preset, string VectorName, VectorOutcome Outcome, string? Detail);

public static class ConsensusSpecTestSummary
{
    private static readonly ConcurrentBag<VectorRecord> Records = [];

    public static void Record(string suite, string fork, string preset, string vectorName, VectorOutcome outcome, string? detail = null) =>
        Records.Add(new VectorRecord(suite, fork, preset, vectorName, outcome, detail));

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
        catch (Exception ex)
        {
            Record(suite, fork, preset, vectorName, VectorOutcome.Fail, ex is AssertionException ? ex.Message : ex.ToString());
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
            // The NUnit adapter may lose teardown console output; retain a best-effort report beside the assembly.
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "consensus-spec-report.txt"), report);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class NotImplementedInDriverException(string message) : Exception(message);
