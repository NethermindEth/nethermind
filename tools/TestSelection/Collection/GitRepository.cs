// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.TestSelection.Collection;

/// <summary>The local clone, read through git; commits that a shallow or partial clone lacks are fetched on demand.</summary>
public sealed class GitRepository(string root)
{
    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    /// <summary>
    /// Files that differ between two commits. Without rename detection a moved file lists both paths, so the project
    /// it left counts as changed.
    /// </summary>
    public async Task<string[]> ChangedFilesAsync(string from, string to, CancellationToken cancellation) =>
        (await RunAsync(["diff", "--no-renames", "--name-only", from, to], cancellation)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public async Task<string> ParentOfAsync(string commit, CancellationToken cancellation)
    {
        await EnsureAsync(commit, cancellation);
        return (await RunAsync(["rev-parse", $"{commit}^"], cancellation)).Trim();
    }

    public async Task<string> MergeBaseWithMasterAsync(string commit, CancellationToken cancellation)
    {
        await EnsureAsync(commit, cancellation);
        return (await RunAsync(["merge-base", "origin/master", commit], cancellation)).Trim();
    }

    private async Task EnsureAsync(string commit, CancellationToken cancellation)
    {
        if (await SucceedsAsync(["cat-file", "-e", $"{commit}^{{commit}}"], cancellation))
        {
            return;
        }

        await _fetchGate.WaitAsync(cancellation);
        try
        {
            await RunAsync(["fetch", "--quiet", "--no-tags", "--filter=blob:none", "origin", commit], cancellation);
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private async Task<bool> SucceedsAsync(string[] arguments, CancellationToken cancellation)
    {
        using Process process = Start(arguments);
        await process.WaitForExitAsync(cancellation);
        return process.ExitCode == 0;
    }

    private async Task<string> RunAsync(string[] arguments, CancellationToken cancellation)
    {
        using Process process = Start(arguments);
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        return process.ExitCode == 0
            ? await output
            : throw new GitException($"git {string.Join(' ', arguments)}: {(await error).Trim()}");
    }

    private Process Start(string[] arguments)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new GitException("git could not start");
    }
}

public sealed class GitException(string message) : Exception(message);
