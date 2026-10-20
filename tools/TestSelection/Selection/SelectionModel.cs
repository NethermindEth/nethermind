// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Nethermind.TestSelection.Files;
using Nethermind.TestSelection.Journal;

namespace Nethermind.TestSelection.Selection;

/// <summary>
/// The selector's links as the model workflow publishes them, with when they were built and how far back the history
/// they come from reaches. A pull request trusts the model only while it is fresh and holds enough history to have
/// learned the links; otherwise every test runs.
/// </summary>
public sealed record SelectionModel(DateTimeOffset BuiltAt, DateTimeOffset? HistoryFrom, SelectorLinks Links)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinHistory = TimeSpan.FromDays(28);

    /// <summary>Why the model at <paramref name="path"/> cannot be trusted at <paramref name="now"/>, or <c>null</c>.</summary>
    public static string? TrustProblem(string path, DateTimeOffset now)
    {
        if (!File.Exists(path))
        {
            return "no selection model";
        }

        SelectionModel model = Read(path);
        if (now - model.BuiltAt > MaxAge)
        {
            return $"the model was built at {model.BuiltAt:O}";
        }

        return model.HistoryFrom is not { } from || now - from < MinHistory
            ? $"the model holds history from {model.HistoryFrom?.ToString("O") ?? "nowhere"} only"
            : null;
    }

    public static SelectionModel Read(string path)
    {
        JsonNode node = JsonFiles.ReadNode(path);
        SelectorLinks links = new();
        foreach ((string project, JsonNode? tests) in node["test_links"]!.AsObject())
        {
            foreach (JsonNode? test in tests!.AsArray())
            {
                links.LinkTest(project, test!.GetValue<string>());
            }
        }

        foreach ((string project, JsonNode? testProjects) in node["project_links"]!.AsObject())
        {
            foreach (JsonNode? testProject in testProjects!.AsArray())
            {
                links.LinkProject(project, testProject!.GetValue<string>());
            }
        }

        foreach (JsonNode? link in node["fork_links"]!.AsArray())
        {
            string project = link![0]!.GetValue<string>();
            string fork = link[1]!.GetValue<string>();
            foreach (JsonNode? broken in link[2]!.AsArray())
            {
                links.LinkFork(project, fork, DateTimeOffset.Parse(broken![0]!.GetValue<string>()), broken[1]!.GetValue<string>());
            }
        }

        string? historyFrom = node["history_from"]?.GetValue<string>();
        return new SelectionModel(DateTimeOffset.Parse(node["built_at"]!.GetValue<string>()), historyFrom is null ? null : DateTimeOffset.Parse(historyFrom), links);
    }

    public void Write(string path)
    {
        JsonObject node = new()
        {
            ["built_at"] = BuiltAt.ToString("O"),
            ["history_from"] = HistoryFrom?.ToString("O"),
            ["test_links"] = LinksNode(Links.Tests),
            ["project_links"] = LinksNode(Links.TestProjects),
            ["fork_links"] = new JsonArray([.. Links.Forks.Select(static link => (JsonNode)new JsonArray(
                link.Key.Project,
                link.Key.Fork,
                new JsonArray([.. link.Value.Select(static broken => (JsonNode)new JsonArray(broken.At.ToString("O"), broken.Test))])))]),
        };
        JsonFiles.Write(path, node);
    }

    private static JsonObject LinksNode(IReadOnlyDictionary<string, HashSet<string>> links) =>
        new(links.Select(static pair => KeyValuePair.Create(pair.Key, (JsonNode?)new JsonArray([.. pair.Value.Order(StringComparer.Ordinal).Select(static name => (JsonNode)name)]))));
}
