// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nethermind.TestSelection.Costs;
using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.Matrices;

namespace Nethermind.TestSelection.Commands;

/// <summary>Writes one nethtest --filterFile per EEST lane and the reduced nethtest matrix for one pull request.</summary>
public static partial class SelectEestCommand
{
    public static Command Create()
    {
        Option<string> model = new("--model") { Required = true };
        Option<string> changedFiles = new("--changed-files") { Required = true, Description = "One changed path per line" };
        Option<string> run = new("--run") { Required = true, Description = "The workflow run id; seeds the sample and the holdout" };
        Option<string> inventory = new("--inventory") { Required = true };
        Option<string> matrix = new("--matrix") { Required = true, Description = "The nethtest matrix, {include: [...]}" };
        Option<string> outMatrix = new("--out-matrix") { Required = true };
        Option<string> filters = new("--filters") { Required = true, Description = "Directory for one --filterFile per lane" };
        Command command = new("select-eest", "Write the nethtest filter files and the reduced EEST matrix")
        {
            model, changedFiles, run, inventory, matrix, outMatrix, filters,
        };
        command.SetAction(result =>
        {
            JsonObject full = JsonFiles.ReadNode(result.GetValue(matrix)!).AsObject();
            PullRequestDecision decision = PullRequestSelection.Decide(
                RepositoryRoot.Find(), result.GetValue(model)!, result.GetValue(changedFiles)!, result.GetValue(run)!, DateTimeOffset.UtcNow);
            if (decision.Selection is null)
            {
                JsonFiles.Write(result.GetValue(outMatrix)!, full);
                Console.WriteLine($"running every EEST test: {decision.Reason}");
                return;
            }

            IReadOnlyDictionary<string, LaneSelection> lanes = CostInventory.Load(result.GetValue(inventory)!).Lanes(decision.Selection);
            double share = lanes.Values.Sum(static lane => lane.Picked) / lanes.Values.Sum(static lane => lane.Cost);
            WriteFilters(result.GetValue(filters)!, lanes);
            JsonObject reduced = NethtestMatrix.Shrink(full, lanes);
            JsonFiles.Write(result.GetValue(outMatrix)!, reduced);
            Console.WriteLine($"selected {100 * share:F1}% of the EEST cost: {reduced["include"]!.AsArray().Count} of {full["include"]!.AsArray().Count} jobs");
        });
        return command;
    }

    public static void WriteFilters(string directory, IReadOnlyDictionary<string, LaneSelection> lanes)
    {
        Directory.CreateDirectory(directory);
        foreach ((string lane, LaneSelection selection) in lanes)
        {
            if (!Lane().IsMatch(lane))
            {
                throw new InvalidDataException($"invalid lane {lane}");
            }

            if (!selection.IsEmpty)
            {
                File.WriteAllLines(Path.Combine(directory, $"{lane.Replace('|', '-')}.txt"), selection.FilterLines());
            }
        }
    }

    // Lane names become file names, so anything but two words would be a path.
    [GeneratedRegex(@"^\w+\|\w+$")]
    private static partial Regex Lane();
}
