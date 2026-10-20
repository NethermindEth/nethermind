// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Nethermind.TestSelection.Costs;

namespace Nethermind.TestSelection.Matrices;

/// <summary>The EEST nethtest matrix, <c>{include: [{test_type, test_setup, chunk}, ...]}</c>.</summary>
public static class NethtestMatrix
{
    /// <summary>
    /// Keeps, per lane, the share of its chunks that matches the share of its cost selected; lanes without a selected
    /// test go, and lanes the inventory does not know stay whole. GitHub rejects an empty matrix, so a selection
    /// that keeps nothing keeps everything.
    /// </summary>
    public static JsonObject Shrink(JsonObject matrix, IReadOnlyDictionary<string, LaneSelection> lanes)
    {
        JsonArray include = [];
        foreach (IGrouping<string, JsonObject> lane in matrix["include"]!.AsArray().Select(static e => e!.AsObject()).GroupBy(LaneOf))
        {
            if (!lanes.TryGetValue(lane.Key, out LaneSelection? selection))
            {
                foreach (JsonObject entry in lane)
                {
                    include.Add(entry.DeepClone());
                }

                continue;
            }

            JsonObject[] entries = [.. lane];
            int chunks = selection.Picked > 0 ? (int)Math.Ceiling(entries.Length * selection.Picked / selection.Cost) : 0;
            for (int i = 0; i < chunks; i++)
            {
                JsonObject entry = entries[i].DeepClone().AsObject();
                entry["chunk"] = $"{i + 1}of{chunks}";
                include.Add(entry);
            }
        }

        return include.Count > 0 ? new JsonObject { ["include"] = include } : matrix.DeepClone().AsObject();
    }

    private static string LaneOf(JsonObject entry) => $"{entry["test_type"]}|{entry["test_setup"]}";
}
