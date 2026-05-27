// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;
using DynamicExpresso;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Nethermind.RpcTests.Monitor;

internal class TestDefinition
{
    public required string FilePath { get; init; }
    public required DynamicJson OnChanged { get; init; }
    public required DynamicJson Request { get; init; }
    public JsonNode? LastOnChangedValue { get; set; }
}

internal static class TestLoader
{
    private static readonly DirectoryInfo _testDir = new("tests");

    private static readonly Parameter[] _parameters =
    [
        new("Head", typeof(HeadInfo)),
        new("Request", typeof(RequestContext))
    ];

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
                foreach (JsonNode? testNode in tests)
                {
                    if (testNode?["onChanged"] is not { } onChangedNode || testNode["request"] is not { } requestNode)
                        continue; // TODO: throw error about invalid test

                    definitions.Add(new TestDefinition
                    {
                        FilePath = path,
                        OnChanged = new DynamicJson(
                            onChangedNode,
                            new Parameter("Head", typeof(HeadInfo))),
                        Request = new DynamicJson(
                            requestNode,
                            new Parameter("Head", typeof(HeadInfo)), new Parameter("Request", typeof(RequestContext))
                        )
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
