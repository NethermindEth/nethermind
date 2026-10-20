// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.Text.Json.Nodes;
using Nethermind.TestSelection.Files;
using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.Matrices;

namespace Nethermind.TestSelection.Commands;

/// <summary>
/// Writes the NUnit matrices of one workflow as step outputs: each job's matrix with only the selected test projects,
/// or null when the job has nothing to run. Every matrix stays whole outside pull requests and whenever the selection
/// cannot be trusted.
/// </summary>
public static class SelectProjectsCommand
{
    public static Command Create()
    {
        SelectionOptions selection = new();
        Option<string> matrices = new("--matrices") { Required = true, Description = "scripts/ci/test-matrices.json" };
        Option<string> workflow = new("--workflow") { Required = true, Description = "The workflow whose matrices to write" };
        Option<string> eventName = new("--event") { Required = true, Description = "github.event_name; only pull requests are selected" };
        Option<string> output = new("--output") { Required = true, Description = "The step output file, $GITHUB_OUTPUT" };
        Command command = new("select-projects", "Write the NUnit matrices of a workflow with only the selected projects")
        {
            matrices, workflow, eventName, output,
        };
        selection.AddTo(command);
        command.SetAction(result =>
        {
            JsonObject workflowMatrices = JsonFiles.ReadNode(result.GetValue(matrices)!)[result.GetValue(workflow)!]!.AsObject();
            PullRequestDecision decision = result.GetValue(eventName) == JournalEntry.PullRequest
                ? selection.Decide(result)
                : new PullRequestDecision(null, "not a pull request");
            File.AppendAllText(result.GetValue(output)!, Outputs(workflowMatrices, decision));
        });
        return command;
    }

    /// <summary>The <c>matrices</c>, <c>any</c> and <c>selected</c> step outputs, and the decision on the console.</summary>
    public static string Outputs(JsonObject workflowMatrices, PullRequestDecision decision)
    {
        JsonObject matrices = [];
        if (decision.Selection is { } selection)
        {
            foreach ((string job, JsonNode? matrix) in workflowMatrices)
            {
                matrices[job] = TestMatrix.Filter(matrix!.AsObject(), selection.KeepsProject);
            }

            string[] kept = [.. TestMatrix.AllProjects(workflowMatrices).Where(selection.KeepsProject).Order(StringComparer.Ordinal)];
            Console.WriteLine($"running {kept.Length} test projects: {string.Join(", ", kept)}");
        }
        else
        {
            matrices = workflowMatrices.DeepClone().AsObject();
            Console.WriteLine($"running every test project: {decision.Reason}");
        }

        bool any = matrices.Any(static pair => pair.Value is not null);
        return $"matrices={matrices.ToJsonString()}\nany={(any ? "true" : "false")}\nselected={(decision.Selection is null ? "false" : "true")}\n";
    }
}
