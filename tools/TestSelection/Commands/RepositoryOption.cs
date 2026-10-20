// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Nethermind.TestSelection.Commands;

/// <summary>The GitHub repository, <c>owner/name</c>; the workflow's own repository by default.</summary>
public static class RepositoryOption
{
    public static readonly Option<string> Instance = new("--repo")
    {
        Recursive = true,
        Description = "The GitHub repository, owner/name",
        DefaultValueFactory = static _ => Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "NethermindEth/nethermind",
    };
}
