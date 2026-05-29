// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Nethermind.RpcTests.Monitor;

internal class TestDefinition
{
    public required string FilePath { get; init; }
    public required DynamicJson<TestContext> OnChanged { get; init; }
    public required DynamicJson<TestContext> Request { get; init; }
    public required TestContext BaseContext { get; init; }
    public JsonNode? LastOnChangedValue { get; set; }
}

internal static class TestLoader
{
    private static readonly DirectoryInfo _testDir = new("tests");

    public static TestDefinition[] Load(string[] globs)
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
                {
                    if (testNode?["onChanged"] is not { } onChangedNode || testNode["request"] is not { } requestNode)
                        continue;

                    int shift = HashCode.Combine(path, testIndex++);
                    TestContext baseContext = new(new BlockInfo(0, ""), new RequestContext(0), shift);

                    definitions.Add(new TestDefinition
                    {
                        FilePath = path,
                        BaseContext = baseContext,
                        OnChanged = new DynamicJson<TestContext>(onChangedNode),
                        Request = new DynamicJson<TestContext>(requestNode)
                    });
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load {path}: {ex.Message}");
            }
        }

        return [.. definitions];
    }
}
