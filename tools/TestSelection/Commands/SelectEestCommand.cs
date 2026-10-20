// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nethermind.TestSelection.Costs;
using Nethermind.TestSelection.Files;
using Nethermind.TestSelection.Journal;
using Nethermind.TestSelection.Matrices;

namespace Nethermind.TestSelection.Commands;

/// <summary>Writes one nethtest --filterFile per EEST lane and the reduced nethtest matrix for one pull request.</summary>
public static partial class SelectEestCommand
{
    public static Command Create()
    {
        SelectionOptions selection = new();
        Option<string> inventory = new("--inventory") { Required = true };
        Option<string> matrix = new("--matrix") { Required = true, Description = "The nethtest matrix, {include: [...]}" };
        Option<string> outMatrix = new("--out-matrix") { Required = true };
        Option<string> filters = new("--filters") { Required = true, Description = "Directory for one --filterFile per lane" };
        Command command = new("select-eest", "Write the nethtest filter files and the reduced EEST matrix") { inventory, matrix, outMatrix, filters };
        selection.AddTo(command);
        command.SetAction(result => Console.WriteLine(Write(
            selection.Decide(result),
            JsonFiles.ReadNode(result.GetValue(matrix)!).AsObject(),
            () => CostInventory.Load(result.GetValue(inventory)!),
            result.GetValue(outMatrix)!,
            result.GetValue(filters)!)));
        return command;
    }

    /// <summary>
    /// Writes the matrix to run and, for a selection, one filter file per lane; returns what was selected. Without a
    /// selection the full matrix runs and the costs are not read.
    /// </summary>
    public static string Write(PullRequestDecision decision, JsonObject full, Func<CostInventory> inventory, string outMatrix, string filters)
    {
        if (decision.Selection is null)
        {
            JsonFiles.Write(outMatrix, full);
            return $"running every EEST test: {decision.Reason}";
        }

        IReadOnlyDictionary<string, LaneSelection> lanes = inventory().Lanes(decision.Selection);
        double share = lanes.Values.Sum(static lane => lane.Picked) / lanes.Values.Sum(static lane => lane.Cost);
        WriteFilters(filters, lanes);
        JsonObject reduced = NethtestMatrix.Shrink(full, lanes);
        JsonFiles.Write(outMatrix, reduced);
        return $"selected {100 * share:F1}% of the EEST cost: {reduced["include"]!.AsArray().Count} of {full["include"]!.AsArray().Count} jobs";
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
