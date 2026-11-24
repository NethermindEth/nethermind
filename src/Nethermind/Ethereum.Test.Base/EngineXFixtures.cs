// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Serialization.Json;

namespace Ethereum.Test.Base;

/// <summary>One test of a <c>blockchain_tests_engine_x</c> fixture file, as the byte range of its top-level property.</summary>
public sealed record EngineXTestRef(string File, long Offset, int Length);

/// <summary>The tests that share one <c>blockchain_tests_engine_x</c> pre-allocation group, in fixture file order.</summary>
public sealed record EngineXGroup(string PreHash, string PreAllocFile, IReadOnlyList<EngineXTestRef> Tests);

/// <summary>Indexes and loads <c>blockchain_tests_engine_x</c> fixtures, whose tests share a genesis per pre-allocation group.</summary>
/// <remarks>
/// A test names its group by <c>preHash</c>; the group's genesis header and allocation live in
/// <c>pre_alloc/&lt;preHash&gt;.json</c> beside the fork directories. Only byte ranges are kept per test, so a whole fork
/// can be indexed while each group's tests are parsed only when the group runs.
/// </remarks>
public static class EngineXFixtures
{
    public const string PreAllocDirectory = "pre_alloc";

    private static readonly EthereumJsonSerializer _serializer = new();
    private static readonly char[] _invalidGroupNameChars = [.. Path.GetInvalidFileNameChars(), '\\'];

    /// <summary>Groups the tests of <paramref name="files"/> by pre-allocation group, in order of first appearance.</summary>
    /// <returns>The groups, and a failed test for each file that could not be indexed or whose group file is missing.</returns>
    public static (List<EngineXGroup> Groups, List<FailedToLoadTest> Failures) Index(IEnumerable<string> files)
    {
        Dictionary<(string Directory, string PreHash), List<EngineXTestRef>> indexedGroups = [];
        List<EngineXGroup> groups = [];
        List<FailedToLoadTest> failures = [];
        Dictionary<string, string?> preAllocDirectories = [];

        foreach (string file in files)
        {
            try
            {
                string? fileDirectory = Path.GetDirectoryName(Path.GetFullPath(file));
                string preAllocDirectory = FindPreAllocDirectory(fileDirectory, preAllocDirectories)
                    ?? throw new DirectoryNotFoundException($"No {PreAllocDirectory} directory above {file}.");

                List<(string PreHash, EngineXTestRef Ref)> tests = ScanFile(file);
                // Every group is checked before any test is added, so a file with a missing group runs none of its tests.
                foreach ((string preHash, _) in tests)
                {
                    if (!indexedGroups.ContainsKey((preAllocDirectory, preHash)))
                    {
                        string preAllocFile = Path.Combine(preAllocDirectory, preHash + ".json");
                        if (!File.Exists(preAllocFile))
                            throw new FileNotFoundException($"Pre-allocation group {preHash} not found.", preAllocFile);
                    }
                }

                foreach ((string preHash, EngineXTestRef testRef) in tests)
                {
                    if (!indexedGroups.TryGetValue((preAllocDirectory, preHash), out List<EngineXTestRef>? members))
                    {
                        members = [];
                        indexedGroups.Add((preAllocDirectory, preHash), members);
                        groups.Add(new EngineXGroup(preHash, Path.Combine(preAllocDirectory, preHash + ".json"), members));
                    }

                    members.Add(testRef);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                failures.Add(new FailedToLoadTest { Name = file, LoadFailure = e.Message });
            }
        }

        return (groups, failures);
    }

    /// <summary>Parses the tests in the byte range <paramref name="testRef"/> names, applying the fixture exclusions.</summary>
    /// <returns>The tests, or a single <see cref="FailedToLoadTest"/> when the range cannot be read or converted.</returns>
    public static IEnumerable<EthereumTest> LoadTests(EngineXTestRef testRef)
    {
        try
        {
            byte[] json = new byte[testRef.Length + 2];
            using (FileStream stream = File.OpenRead(testRef.File))
            {
                stream.Seek(testRef.Offset, SeekOrigin.Begin);
                stream.ReadExactly(json.AsSpan(1, testRef.Length));
            }

            json[0] = (byte)'{';
            json[^1] = (byte)'}';
            return [.. new FileTestsSource(testRef.File).LoadTests(json, TestType.Blockchain)];
        }
        catch (Exception e)
        {
            return [new FailedToLoadTest { Name = $"{testRef.File}@{testRef.Offset}", LoadFailure = $"Failed to load: {e}" }];
        }
    }

    /// <summary>
    /// Creates the test that sets up the client of <paramref name="group"/>: the fork schedule of <paramref name="member"/>,
    /// any test of the group, with the group's genesis header and allocation.
    /// </summary>
    public static BlockchainTest CreateGroupTest(EngineXGroup group, BlockchainTest member)
    {
        PreAllocGroupJson preAlloc = _serializer.Deserialize<PreAllocGroupJson>(File.ReadAllText(group.PreAllocFile))
            ?? throw new InvalidDataException($"Empty pre-allocation group {group.PreAllocFile}.");

        return new BlockchainTest
        {
            Name = group.PreHash,
            Category = member.Category,
            ForkName = member.ForkName,
            Network = member.Network,
            NetworkAfterTransition = member.NetworkAfterTransition,
            TransitionForkActivation = member.TransitionForkActivation,
            TransitionTerminalTotalDifficulty = member.TransitionTerminalTotalDifficulty,
            ChainId = member.ChainId,
            GenesisBlockHeader = preAlloc.Genesis ?? throw new InvalidDataException($"No genesis in {group.PreAllocFile}."),
            Pre = preAlloc.Pre ?? throw new InvalidDataException($"No pre-allocation in {group.PreAllocFile}."),
        };
    }

    private static string? FindPreAllocDirectory(string? directory, Dictionary<string, string?> cache)
    {
        if (directory is null)
            return null;
        if (cache.TryGetValue(directory, out string? cached))
            return cached;

        string candidate = Path.Combine(directory, PreAllocDirectory);
        string? found = Directory.Exists(candidate) ? candidate : FindPreAllocDirectory(Path.GetDirectoryName(directory), cache);
        cache[directory] = found;
        return found;
    }

    private static List<(string PreHash, EngineXTestRef Ref)> ScanFile(string file)
    {
        byte[] json = File.ReadAllBytes(file);
        Utf8JsonReader reader = new(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException($"Expected a fixture object in {file}.");

        List<(string, EngineXTestRef)> tests = [];
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            long offset = reader.TokenStartIndex;
            string testName = reader.GetString()!;
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException($"Test {testName} in {file} is not an object.");

            string? preHash = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isPreHash = reader.ValueTextEquals("preHash"u8);
                reader.Read();
                if (isPreHash)
                    preHash = reader.TokenType == JsonTokenType.String
                        ? reader.GetString()
                        : throw new InvalidDataException($"Test {testName} in {file} has a preHash that is not a string.");
                else
                    reader.Skip();
            }

            if (string.IsNullOrEmpty(preHash) || preHash.IndexOfAny(_invalidGroupNameChars) >= 0)
                throw new InvalidDataException($"Test {testName} in {file} has a missing or invalid preHash.");
            tests.Add((preHash,
                new EngineXTestRef(file, offset, checked((int)(reader.BytesConsumed - offset)))));
        }

        if (reader.Read())
            throw new InvalidDataException($"Unexpected data after the fixture object in {file}.");
        return tests;
    }

    private sealed class PreAllocGroupJson
    {
        public TestBlockHeaderJson? Genesis { get; set; }
        public Dictionary<Address, AccountState>? Pre { get; set; }
    }
}
