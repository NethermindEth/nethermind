// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

/// <summary>
/// Loads devnet genesis files as the node does and checks that every fork they schedule activates at its time.
/// </summary>
/// <remarks>
/// Reads the genesis files in the directory named by <see cref="GenesisDirectoryVariable"/> and is ignored when it is unset.
/// The <c>check-devnet-genesis.yml</c> workflow fetches the latest ethpandaops devnet genesis files and sets it.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class GethGenesisDevnetTests
{
    /// <summary>Environment variable naming a directory of genesis files to check.</summary>
    public const string GenesisDirectoryVariable = "NETHERMIND_GETH_GENESIS_DIR";

    private static readonly HashSet<string> GethConfigFields = new(
        typeof(GethGenesisConfigJson).GetProperties().Select(static p => p.Name), StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<TestCaseData> GenesisFiles()
    {
        if (Environment.GetEnvironmentVariable(GenesisDirectoryVariable) is not { Length: > 0 } directory)
        {
            yield return new TestCaseData("").SetArgDisplayNames("<unset>").Ignore($"{GenesisDirectoryVariable} is not set");
            yield break;
        }

        string[] files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : [];
        if (files.Length == 0)
        {
            // Surfaces a broken fetch instead of passing on nothing.
            yield return new TestCaseData(Path.Combine(directory, "<no genesis files>")).SetArgDisplayNames("<none>");
        }

        foreach (string path in files.Order(StringComparer.Ordinal))
        {
            yield return new TestCaseData(path).SetArgDisplayNames(Path.GetFileName(path));
        }
    }

    [TestCaseSource(nameof(GenesisFiles))]
    public void Devnet_genesis_loads_and_activates_its_forks(string path)
    {
        Assert.That(File.Exists(path), $"{path} does not exist");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement config = document.RootElement.GetProperty("config");
        ulong genesisTimestamp = ReadUlong(document.RootElement.GetProperty("timestamp"));

        ChainSpec chainSpec;
        using (FileStream stream = File.OpenRead(path))
        {
            chainSpec = new AutoDetectingChainSpecLoader(new EthereumJsonSerializer(), LimboLogs.Instance).Load(stream);
        }

        ChainSpecBasedSpecProvider provider = new(chainSpec);
        Dictionary<string, string> forksByKey = GethGenesisParityTests.GethTimeKeysByFork.Value.ToDictionary(static p => p.Value, static p => p.Key, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, NamedReleaseSpec> forkClasses = GethGenesisParityTests.MainnetForks.Value.ToDictionary(static f => f.GetType().Name, StringComparer.OrdinalIgnoreCase);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainSpec.ChainId, Is.EqualTo(ReadUlong(config.GetProperty("chainId"))), "chainId");

            foreach (JsonProperty field in config.EnumerateObject())
            {
                Assert.That(GethConfigFields.Contains(field.Name), $"config.{field.Name} is not a field Nethermind reads, so it is ignored");

                if (!forksByKey.TryGetValue(field.Name, out string? forkName)) continue;

                NamedReleaseSpec fork = forkClasses[forkName];
                ulong time = Math.Max(ReadUlong(field.Value), genesisTimestamp);
                IReleaseSpec atFork = provider.GetSpec(new ForkActivation(1, time));
                Assert.That(MissingEips(fork, atFork), Is.Empty, $"{field.Name} = {time}: {fork.Name} EIPs not enabled");

                if (time > genesisTimestamp && fork.Parent is { } parent)
                {
                    IReleaseSpec beforeFork = provider.GetSpec(new ForkActivation(1, time - 1));
                    Assert.That(MissingEips(fork, beforeFork), Is.SupersetOf(MissingEips(fork, parent)), $"{field.Name} = {time}: {fork.Name} EIPs enabled early");
                }
            }

            if (config.TryGetProperty("blobSchedule", out JsonElement blobSchedule))
            {
                CheckBlobSchedule(config, blobSchedule, provider, genesisTimestamp, forksByKey, forkClasses);
            }
        }
    }

    private static void CheckBlobSchedule(JsonElement config, JsonElement blobSchedule, ChainSpecBasedSpecProvider provider, ulong genesisTimestamp,
        Dictionary<string, string> forksByKey, Dictionary<string, NamedReleaseSpec> forkClasses)
    {
        Dictionary<string, ulong> times = [];
        foreach (JsonProperty entry in blobSchedule.EnumerateObject())
        {
            string timeKey = entry.Name + "Time";
            bool honoured = forksByKey.TryGetValue(timeKey, out string? fork) && GethGenesisParityTests.IsBlobScheduleKeyHonoured(forkClasses[fork]);
            Assert.That(honoured, $"blobSchedule.{entry.Name} is not a key Nethermind reads, so it is ignored");
            if (honoured && config.TryGetProperty(timeKey, out JsonElement time)) times[entry.Name] = Math.Max(ReadUlong(time), genesisTimestamp);
        }

        // Where several entries share a timestamp the loader applies the one with the highest precedence.
        foreach (IGrouping<ulong, string> atTime in times.GroupBy(static t => t.Value, static t => t.Key))
        {
            string key = atTime.MaxBy(k => GethGenesisLoader.BlobScheduleOrder(forksByKey[k + "Time"]))!;
            JsonElement entry = blobSchedule.GetProperty(key);
            IReleaseSpec spec = provider.GetSpec(new ForkActivation(1, atTime.Key));
            Assert.That((spec.TargetBlobCount, spec.MaxBlobCount, spec.BlobBaseFeeUpdateFraction),
                Is.EqualTo((ReadUlong(entry.GetProperty("target")), ReadUlong(entry.GetProperty("max")), ReadUlong(entry.GetProperty("baseFeeUpdateFraction")))),
                $"blobSchedule.{key} at {atTime.Key}");
        }
    }

    /// <summary>Runs the devnet checks on a synthetic genesis, so they are exercised without <see cref="GenesisDirectoryVariable"/>.</summary>
    [TestCase(nameof(Osaka))]
    [TestCase(nameof(BPO2))]
    [TestCase(nameof(Bogota))]
    public void Synthetic_genesis_passes_devnet_checks(string forkName)
    {
        NamedReleaseSpec fork = GethGenesisParityTests.MainnetForks.Value.Single(f => f.GetType().Name == forkName);
        string path = Path.Combine(Path.GetTempPath(), $"{nameof(GethGenesisDevnetTests)}-{Guid.NewGuid():N}.json");
        // Own blobSchedule entry with values no fork class has, so ignoring the key fails the check.
        string blobKey = GethGenesisParityTests.StripTime(GethGenesisParityTests.GethTimeKeysByFork.Value[forkName]);
        File.WriteAllText(path, GethGenesisParityTests.BuildGethGenesis(fork, (blobKey, 7, 11, 1234567)));
        try
        {
            Devnet_genesis_loads_and_activates_its_forks(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Missing_eips_includes_inherited_flags(
        [Values(nameof(IReceiptSpec.IsEip7778Enabled), nameof(IEip1559Spec.IsEip1559Enabled))] string property,
        [Values] bool enabled)
    {
        OverridableReleaseSpec spec = new(Amsterdam.Instance);
        typeof(OverridableReleaseSpec).GetProperty(property)!.SetValue(spec, enabled);

        Assert.That(MissingEips(Amsterdam.Instance, spec).Contains(property), Is.EqualTo(!enabled));
    }

    /// <summary>EIP flags <paramref name="fork"/> enables that <paramref name="spec"/> does not.</summary>
    private static List<string> MissingEips(NamedReleaseSpec fork, IReleaseSpec spec) =>
    [
        .. GethGenesisParityTests.SpecProperties
            .Where(static p => p.PropertyType == typeof(bool) && p.Name.StartsWith("IsEip", StringComparison.Ordinal))
            .Where(p => (bool)p.GetValue(fork)! && !(bool)p.GetValue(spec)!)
            .Select(static p => p.Name)
    ];

    private static ulong ReadUlong(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetUInt64(),
        JsonValueKind.String when value.GetString() is { } s && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => Convert.ToUInt64(s[2..], 16),
        JsonValueKind.String => ulong.Parse(value.GetString()!),
        _ => throw new FormatException($"expected a number, got {value.ValueKind}"),
    };
}
