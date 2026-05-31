// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Nethermind.RpcTests.Monitor;

internal static class TestLoader
{
    private static readonly DirectoryInfo _testDir = new("tests");

    public static TestDefinition[] Load(string[] globs, bool requiresResponse)
    {
        Matcher matcher = new();
        matcher.AddIncludePatterns(globs);
        PatternMatchingResult matches = matcher.Execute(new DirectoryInfoWrapper(_testDir));

        List<TestDefinition> definitions = [];
        foreach (string path in matches.Files.Select(static f => f.Path))
        {
            try
            {
                string fullPath = Path.Combine(_testDir.Name, path);
                JsonArray tests = JsonNode.Parse(File.ReadAllText(fullPath))!.AsArray();
                int testIndex = 0;

                foreach (JsonNode? testNode in tests)
                    definitions.Add(new TestDefinition(++testIndex, path, testNode!, requiresResponse));
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to load test definition: " + ex.Message, ex);
            }
        }

        return [.. definitions];
    }
}
