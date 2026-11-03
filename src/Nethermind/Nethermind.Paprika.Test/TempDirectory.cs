// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Paprika.Tests;

/// <summary>
/// A new temporary directory, deleted with its content on dispose.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("paprika-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
