// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Nethermind.TestSelection.GitHub;
using Nethermind.TestSelection.Selection;

namespace Nethermind.TestSelection.Collection;

/// <summary>
/// The files a run changed: a pull request against its merge base with master, a master push (one squash merge)
/// against its parent. The local clone answers; the compare API is the fallback for commits it cannot fetch.
/// </summary>
public sealed class ChangedFiles(GitRepository git, IGitHubApi api)
{
    // The compare API lists at most this many files; a longer diff counts as touching everything.
    private const int CompareFileCap = 300;

    public async Task<IReadOnlyList<string>> OfRunAsync(JsonNode run, CancellationToken cancellation)
    {
        string head = run["head_sha"]!.GetValue<string>();
        bool push = run["event"]!.GetValue<string>() == "push";
        try
        {
            string from = push ? await git.ParentOfAsync(head, cancellation) : await git.MergeBaseWithMasterAsync(head, cancellation);
            return await git.ChangedFilesAsync(from, head, cancellation);
        }
        catch (GitException)
        {
            return await FromApiAsync(run, head, push, cancellation);
        }
    }

    /// <summary>Paths of the files the API lists for a diff, with the old path of each renamed file.</summary>
    public static IReadOnlyList<string> PathsOf(JsonArray files)
    {
        List<string> paths = [];
        foreach (JsonNode? file in files)
        {
            paths.Add(file!["filename"]!.GetValue<string>());
            if (file["previous_filename"]?.GetValue<string>() is { } previous)
            {
                paths.Add(previous);
            }
        }

        return paths;
    }

    private async Task<IReadOnlyList<string>> FromApiAsync(JsonNode run, string head, bool push, CancellationToken cancellation)
    {
        string range;
        if (push)
        {
            JsonNode commit = await api.GetAsync($"repos/{api.Repository}/commits/{head}", cancellation);
            range = $"{commit["parents"]![0]!["sha"]!.GetValue<string>()}...{head}";
        }
        else
        {
            range = $"master...{run["head_repository"]!["owner"]!["login"]!.GetValue<string>()}:{head}";
        }

        JsonNode compare = await api.GetAsync($"repos/{api.Repository}/compare/{range}", cancellation);
        IReadOnlyList<string> files = PathsOf(compare["files"]?.AsArray() ?? []);
        return files.Count >= CompareFileCap ? [RepositoryProjects.UnknownChanges] : files;
    }
}
