// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Nethermind.TestSelection.Journal;

namespace Nethermind.TestSelection.Collection;

/// <summary>The NUnit test workflows: the keys of scripts/ci/test-matrices.json, so the list lives in one place.</summary>
public static class TestWorkflows
{
    public const string MatricesFile = "scripts/ci/test-matrices.json";

    public static IReadOnlyList<string> Read(string root) =>
        [.. JsonFiles.ReadNode(Path.Combine(root, MatricesFile)).AsObject().Select(static pair => $"{pair.Key}.yml")];
}
