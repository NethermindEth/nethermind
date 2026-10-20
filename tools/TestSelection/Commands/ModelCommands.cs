// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.TestSelection.Collection;
using Nethermind.TestSelection.GitHub;
using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.MasterFailures;
using Nethermind.TestSelection.Selection;

namespace Nethermind.TestSelection.Commands;

/// <summary>The commands that read the published model: download it, and report a red master run.</summary>
public static class ModelCommands
{
    public static Command Download()
    {
        Option<string> output = new("--out") { Required = true };
        Command command = new("download", "Extract the newest trusted model artifact; nothing when there is none") { output };
        command.SetAction(async (result, cancellation) =>
        {
            using GitHubClient github = GitHubClient.FromEnvironment(result.GetValue(RepositoryOption.Instance)!);
            long? run = await new ModelArtifact(new ActionsApi(github)).DownloadAsync(result.GetValue(output)!, cancellation);
            Console.WriteLine(run is { } id ? $"model of run {id}" : "no model artifact");
        });
        return command;
    }

    public static Command Prune()
    {
        Option<int> keep = new("--keep") { Description = "How many of the newest trusted artifacts to keep", DefaultValueFactory = static _ => 2 };
        Command command = new("prune", "Delete the superseded model artifacts of master runs") { keep };
        command.SetAction(async (result, cancellation) =>
        {
            using GitHubClient github = GitHubClient.FromEnvironment(result.GetValue(RepositoryOption.Instance)!);
            Console.WriteLine($"deleted {await new ModelArtifact(new ActionsApi(github)).PruneAsync(result.GetValue(keep), cancellation)} artifacts");
        });
        return command;
    }

    public static Command ReportMasterFailure()
    {
        Option<long> run = new("--run") { Required = true, Description = "A completed master run of a test workflow" };
        Option<string> journal = new("--journal") { Required = true };
        Option<string> model = new("--model") { Required = true };
        Command command = new("report-master-failure", "Open, update or close the issue for a master run of a test workflow")
        {
            run, journal, model,
        };
        command.SetAction(async (result, cancellation) =>
        {
            using GitHubClient github = GitHubClient.FromEnvironment(result.GetValue(RepositoryOption.Instance)!);
            string modelPath = result.GetValue(model)!;
            SelectorLinks links = File.Exists(modelPath) ? SelectionModel.Read(modelPath).Links : new SelectorLinks();
            MasterFailureReporter reporter = new(github, new RunFailures(new ActionsApi(github)), new IssueTracker(github),
                Projects.OfRepository(RepositoryRoot.Find()), links);
            Console.WriteLine(await reporter.ReportAsync(result.GetValue(run), RunJournal.Read(result.GetValue(journal)!), cancellation));
        });
        return command;
    }
}
