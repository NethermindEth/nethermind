// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.Selection;

namespace Nethermind.TestSelection.Test;

/// <summary>Projects, runs and journal entries shared by the tests.</summary>
internal static class Fixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly string Core = Projects.DirectoryOf("Nethermind.Core");
    public static readonly string Evm = Projects.DirectoryOf("Nethermind.Evm");
    public static readonly string JsonRpc = Projects.DirectoryOf("Nethermind.JsonRpc");
    public static readonly string Config = Projects.DirectoryOf("Nethermind.Config");
    public static readonly string EvmTest = Projects.DirectoryOf("Nethermind.Evm.Test");
    public static readonly string JsonRpcTest = Projects.DirectoryOf("Nethermind.JsonRpc.Test");

    /// <summary>Evm references Core; Evm.Test references Evm; JsonRpc.Test references JsonRpc; Config includes the chain specs.</summary>
    public static Projects Projects { get; } = new(
        new Dictionary<string, IReadOnlySet<string>>
        {
            [Core] = new HashSet<string>(),
            [Evm] = new HashSet<string> { Core },
            [JsonRpc] = new HashSet<string>(),
            [Config] = new HashSet<string>(),
            [EvmTest] = new HashSet<string> { Evm },
            [JsonRpcTest] = new HashSet<string> { JsonRpc },
        },
        new Dictionary<string, IReadOnlySet<string>> { [Projects.DirectoryOf("Chains")] = new HashSet<string> { Config } });

    /// <summary>A run id that the selector does not hold out, so a selection applies its rules.</summary>
    public static string RunWithoutHoldout(int start = 1) =>
        Enumerable.Range(start, 1000).Select(static run => run.ToString()).First(static run => !Sampling.IsSampled($"holdout|{run}", Selector.HoldoutRate));

    /// <summary>A run id that is no holdout and whose sample keeps none of the projects.</summary>
    public static string RunSamplingNone(params string[] projects) =>
        Enumerable.Range(1, 10000).Select(static run => run.ToString())
            .First(run => !Sampling.IsSampled($"holdout|{run}", Selector.HoldoutRate) && projects.All(project => Unsampled(run, project)));

    public static bool Unsampled(string run, params string[] key) => !Sampling.IsSampled(string.Join('|', [run, .. key]));

    public static JournalEntry Entry(long run, double days, string branch, string[] files, string[]? failedTests = null, string @event = JournalEntry.PullRequest, bool selected = false) =>
        new(run, Now.AddDays(days), @event, branch, files, failedTests is null ? Failures.None : new Failures(failedTests, []), selected);

    public static string EestFailure(string test, string fork = "Cancun") => $"EEST|blockTest|sequential|{test}|{fork}";

    /// <summary>The pages of a paginated GitHub list.</summary>
    public static async IAsyncEnumerable<JsonNode> Pages(params JsonNode[] pages)
    {
        foreach (JsonNode page in pages)
        {
            yield return page;
        }

        await Task.CompletedTask;
    }
}
