// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.TestSelection.GitHub;

/// <summary>
/// The one artifact the model workflow publishes: the model, the test costs and the journal it learns from. This is
/// the only place that decides which artifact to trust: a master run of this repository. A fork can name its branch
/// master, but its runs have another head repository.
/// </summary>
public sealed class ModelArtifact(ActionsApi actions)
{
    public const string Name = "test-selection-model";

    /// <summary>Extracts the newest trusted artifact into the directory; returns the run it came from, or <c>null</c>.</summary>
    public async Task<long?> DownloadAsync(string directory, CancellationToken cancellation)
    {
        JsonNode? newest = (await actions.ArtifactsNamedAsync(Name, cancellation))
            .Where(IsTrusted)
            .MaxBy(static artifact => DateTimeOffset.Parse(artifact["created_at"]!.GetValue<string>()));
        if (newest is null)
        {
            return null;
        }

        await actions.ExtractArtifactAsync(newest["id"]!.GetValue<long>(), directory, cancellation);
        return newest["workflow_run"]!["id"]!.GetValue<long>();
    }

    public static bool IsTrusted(JsonNode artifact)
    {
        JsonNode? run = artifact["workflow_run"];
        return artifact["expired"]?.GetValue<bool>() == false
            && run?["head_branch"]?.GetValue<string>() == "master"
            && run["head_repository_id"]?.GetValue<long>() is { } head
            && run["repository_id"]?.GetValue<long>() is { } repository
            && head == repository;
    }
}
