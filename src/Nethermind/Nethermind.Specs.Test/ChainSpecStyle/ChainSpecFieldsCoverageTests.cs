// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

/// <summary>
/// Tests to verify that all fields from ChainSpecParamsJson are used in ChainSpecLoader
/// and all fields from ChainSpec/ChainParameters are being set.
/// </summary>
[TestFixture]
public class ChainSpecFieldsCoverageTests
{
    private static string LoadChainSpecLoaderSource()
    {
        // Try to find the ChainSpecLoader.cs file relative to the test directory
        string testDir = TestContext.CurrentContext.TestDirectory;
        string[] possiblePaths = new[]
        {
            Path.Combine(testDir, "../../../../../../../Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs"),
            Path.Combine(testDir, "../../../../../../Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs"),
            Path.Combine(testDir, "../../../../../Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs"),
            Path.Combine(testDir, "../../../../Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs"),
        };

        foreach (string path in possiblePaths)
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                return File.ReadAllText(fullPath);
            }
        }

        // Fallback: search from current working directory
        string cwd = Directory.GetCurrentDirectory();
        string cwdPath = Path.Combine(cwd, "src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecLoader.cs");
        if (File.Exists(cwdPath))
        {
            return File.ReadAllText(cwdPath);
        }

        throw new FileNotFoundException($"Could not find ChainSpecLoader.cs. Searched in test directory: {testDir}, current directory: {cwd}");
    }

    private static readonly string ChainSpecLoaderSource = LoadChainSpecLoaderSource();

    [Test]
    public void All_ChainSpecParamsJson_fields_should_be_accessed_in_ChainSpecLoader()
    {
        // Get all public properties from ChainSpecParamsJson
        PropertyInfo[] properties = typeof(ChainSpecParamsJson).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var unusedProperties = new List<string>();

        foreach (PropertyInfo property in properties)
        {
            // Check if the property is accessed in ChainSpecLoader
            // We look for patterns like:
            // - chainSpecJson.Params.PropertyName
            // - Params.PropertyName
            string[] accessPatterns = new[]
            {
                $".Params.{property.Name}",
                $"Params.{property.Name}"
            };

            bool isAccessed = accessPatterns.Any(pattern => ChainSpecLoaderSource.Contains(pattern));

            if (!isAccessed)
            {
                unusedProperties.Add(property.Name);
            }
        }

        unusedProperties.Should().BeEmpty(
            $"The following properties from ChainSpecParamsJson are not accessed in ChainSpecLoader: {string.Join(", ", unusedProperties)}");
    }

    [Test]
    public void All_ChainSpec_settable_properties_should_be_set_in_ChainSpecLoader()
    {
        // Properties that are set by engine-specific parameters (via ApplyToChainSpec)
        // These are not set directly in ChainSpecLoader but through the engine parameters
        var engineSetProperties = new HashSet<string>
        {
            "FixedDifficulty", // Set by EthashChainSpecEngineParameters
            "DaoForkBlockNumber", // Set by EthashChainSpecEngineParameters
            "MuirGlacierNumber", // Set by EthashChainSpecEngineParameters
            "ArrowGlacierBlockNumber", // Set by EthashChainSpecEngineParameters
            "GrayGlacierBlockNumber" // Set by EthashChainSpecEngineParameters
        };

        // Get all public properties from ChainSpec that have a setter
        PropertyInfo[] properties = typeof(ChainSpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToArray();

        var unsetProperties = new List<string>();

        foreach (PropertyInfo property in properties)
        {
            // Skip properties that are set by engine-specific parameters
            if (engineSetProperties.Contains(property.Name))
            {
                continue;
            }

            // Check if the property is set in ChainSpecLoader
            // We look for patterns like:
            // - chainSpec.PropertyName =
            // - ChainSpec { PropertyName =
            string[] setPatterns = new[]
            {
                $"chainSpec.{property.Name} =",
                $"chainSpec.{property.Name}=",
                $"{property.Name} ="
            };

            bool isSet = setPatterns.Any(pattern => ChainSpecLoaderSource.Contains(pattern));

            if (!isSet)
            {
                unsetProperties.Add(property.Name);
            }
        }

        unsetProperties.Should().BeEmpty(
            $"The following properties from ChainSpec are not set in ChainSpecLoader: {string.Join(", ", unsetProperties)}. " +
            $"If a property is intentionally set by engine-specific parameters, add it to the engineSetProperties list.");
    }

    [Test]
    public void All_ChainParameters_settable_properties_should_be_set_in_ChainSpecLoader()
    {
        // Get all public properties from ChainParameters that have a setter
        PropertyInfo[] properties = typeof(ChainParameters)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToArray();

        var unsetProperties = new List<string>();

        foreach (PropertyInfo property in properties)
        {
            // Check if the property is set in ChainSpecLoader
            // We look for patterns like:
            // - PropertyName =
            // Within the context of ChainParameters initialization
            bool isSet = ChainSpecLoaderSource.Contains($"{property.Name} =") ||
                        ChainSpecLoaderSource.Contains($"{property.Name}=");

            if (!isSet)
            {
                unsetProperties.Add(property.Name);
            }
        }

        unsetProperties.Should().BeEmpty(
            $"The following properties from ChainParameters are not set in ChainSpecLoader: {string.Join(", ", unsetProperties)}");
    }

    [Test]
    public void ChainSpecLoader_should_handle_all_fields_correctly()
    {
        // This is an integration test that loads a sample chainspec
        // and verifies that the loader doesn't throw exceptions
        var serializer = new EthereumJsonSerializer();
        var loader = new ChainSpecLoader(serializer, LimboLogs.Instance);

        // Create a minimal valid chainspec JSON
        string minimalChainSpec = @"{
            ""name"": ""Test"",
            ""engine"": {
                ""Ethash"": {
                    ""params"": {
                        ""minimumDifficulty"": ""0x020000"",
                        ""difficultyBoundDivisor"": ""0x0800"",
                        ""durationLimit"": ""0x0d"",
                        ""homesteadTransition"": ""0x0""
                    }
                }
            },
            ""params"": {
                ""chainId"": ""0x1"",
                ""networkId"": ""0x1""
            },
            ""genesis"": {
                ""seal"": {
                    ""ethereum"": {
                        ""nonce"": ""0x0000000000000042"",
                        ""mixHash"": ""0x0000000000000000000000000000000000000000000000000000000000000000""
                    }
                },
                ""difficulty"": ""0x400000000"",
                ""author"": ""0x0000000000000000000000000000000000000000"",
                ""timestamp"": ""0x00"",
                ""parentHash"": ""0x0000000000000000000000000000000000000000000000000000000000000000"",
                ""extraData"": ""0x11bbe8db4e347b4e8c937c1c8370e4b5ed33adb3db69cbdb7a38e1e50b1b82fa"",
                ""gasLimit"": ""0x1388""
            },
            ""accounts"": {}
        }";

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(minimalChainSpec));
        
        // Should not throw
        Action loadAction = () => loader.Load(stream);
        loadAction.Should().NotThrow();
    }
}
