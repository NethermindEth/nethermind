// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Selection;

namespace Nethermind.TestSelection.Commands;

/// <summary>The checkout this tool runs in: the nearest directory above the working directory that holds the sources.</summary>
public static class RepositoryRoot
{
    public static string Find()
    {
        for (DirectoryInfo? directory = new(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, Projects.SourceRoot)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"run inside the repository: no {Projects.SourceRoot} above {Environment.CurrentDirectory}");
    }
}
