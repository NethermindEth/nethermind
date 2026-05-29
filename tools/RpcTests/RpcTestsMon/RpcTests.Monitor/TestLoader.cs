// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Nethermind.RpcTests.Monitor.Dynamic;

namespace Nethermind.RpcTests.Monitor;

internal class TestDefinition
{
    public required string FilePath { get; init; }
    public required int Index { get; init; }
    public required DynamicExpression<TestContext, bool> Run { get; init; }
    public required DynamicJson<TestContext> Request { get; init; }
    public DynamicJson<TestContext>? Response { get; init; }
}

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
                {
                    if (testNode?["run"]?.GetValue<string>() is not { } runExpr || testNode["request"] is not { } requestNode)
                        throw new Exception("Test is missing required properties 'run' and 'request'");

                    if (requiresResponse && testNode["response"] is null)
                        throw new Exception("Test is missing required 'response' property");

                    definitions.Add(new TestDefinition
                    {
                        FilePath = path,
                        Index = ++testIndex,
                        Run = new DynamicExpression<TestContext, bool>(runExpr),
                        Request = new DynamicJson<TestContext>(requestNode),
                        Response = testNode["response"] is { } responseNode ? new DynamicJson<TestContext>(responseNode) : null
                    });
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load test \"{path}\": {ex.Message}");
                throw;
            }
        }

        return [.. definitions];
    }
}
