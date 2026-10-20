// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.TestSelection.Collection;
using Nethermind.TestSelection.Costs;
using Nethermind.TestSelection.GitHub;
using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.Replay;
using Nethermind.TestSelection.Selection;

namespace Nethermind.TestSelection.Commands;

/// <summary>The commands of the hourly model workflow: collect runs, measure costs, build the model, replay it.</summary>
public static class HistoryCommands
{
    public static Command Collect()
    {
        Option<string> journal = new("--journal") { Required = true };
        Command command = new("collect", "Append the finished test runs to the journal") { journal };
        command.SetAction(async (result, cancellation) =>
        {
            string root = RepositoryRoot.Find();
            using GitHubClient github = GitHubClient.FromEnvironment(result.GetValue(RepositoryOption.Instance)!, GitHubClient.BulkReserveShare);
            ActionsApi actions = new(github);
            RunCollector collector = new(actions, new ChangedFiles(new GitRepository(root), github), new RunFailures(actions), TestWorkflows.Read(root));
            Console.WriteLine($"collected {await collector.CollectAsync(result.GetValue(journal)!, DateTimeOffset.UtcNow, cancellation)} runs");
        });
        return command;
    }

    public static Command Inventory()
    {
        Option<long> run = new("--run") { Required = true, Description = "A full run of nethermind-tests.yml" };
        Option<string> output = new("--out") { Required = true };
        Command command = new("inventory", "Write the cost of every EEST unit and NUnit test project from one full run") { run, output };
        command.SetAction(async (result, cancellation) =>
        {
            using GitHubClient github = GitHubClient.FromEnvironment(result.GetValue(RepositoryOption.Instance)!, GitHubClient.BulkReserveShare);
            string work = Directory.CreateTempSubdirectory("test-selection-").FullName;
            try
            {
                CostInventory inventory = await new InventoryBuilder(new ActionsApi(github)).BuildAsync(result.GetValue(run), work, cancellation);
                inventory.Save(result.GetValue(output)!);
            }
            finally
            {
                Directory.Delete(work, recursive: true);
            }
        });
        return command;
    }

    public static Command Model()
    {
        Option<string> journal = new("--journal") { Required = true };
        Option<string> output = new("--out") { Required = true };
        Command command = new("model", "Replay the journal into the selector's links") { journal, output };
        command.SetAction(result =>
        {
            IReadOnlyList<JournalEntry> entries = RunJournal.Read(result.GetValue(journal)!);
            Selector selector = Selector.Trained(entries, Projects.OfRepository(RepositoryRoot.Find()));
            new SelectionModel(DateTimeOffset.UtcNow, entries.Count > 0 ? entries[0].At : null, selector.Links).Write(result.GetValue(output)!);
            Console.WriteLine($"model from {entries.Count} runs");
        });
        return command;
    }

    public static Command Replay()
    {
        Option<string> journal = new("--journal") { Required = true };
        Option<string> inventory = new("--inventory") { Required = true };
        Option<string> report = new("--report") { Required = true };
        Command command = new("replay", "Report what the selector would have run and hidden on the recorded pull request runs")
        {
            journal, inventory, report,
        };
        command.SetAction(result =>
        {
            IReadOnlyList<JournalEntry> entries = RunJournal.Read(result.GetValue(journal)!);
            if (entries.Count == 0)
            {
                Console.WriteLine("the journal is empty");
                return;
            }

            SelectionReplay replay = new(CostInventory.Load(result.GetValue(inventory)!), Projects.OfRepository(RepositoryRoot.Find()));
            ReplayReport replayReport = replay.Run(entries, entries[0].At + SelectionReplay.Warmup);
            File.WriteAllText(result.GetValue(report)!, replayReport.ToMarkdown(result.GetValue(RepositoryOption.Instance)!, entries));
        });
        return command;
    }
}
