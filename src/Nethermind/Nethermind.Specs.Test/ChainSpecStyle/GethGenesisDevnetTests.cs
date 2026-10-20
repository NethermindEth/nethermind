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
/// Loads real devnet genesis files as the node does and checks that every fork they schedule activates at its time.
/// </summary>
/// <remarks>
/// Vendored copies from the ethpandaops devnet repos, config and header fields verbatim, alloc trimmed to the predeploys
/// and two funded accounts:
/// <list type="bullet">
///   <item><c>heze-devnet-1.json</c>: ethpandaops/heze-devnets@a970f39d48c12598b716eecc4567f6bf2beef7c7, network-configs/devnet-1/metadata/genesis.json</item>
///   <item><c>glamsterdam-devnet-12.json</c>: ethpandaops/glamsterdam-devnets@668df8608fb9bc51b2236fac67193de31c369452, network-configs/devnet-12/metadata/genesis.json</item>
/// </list>
/// The scheduled <c>check-devnet-genesis.yml</c> workflow runs the same checks on the latest upstream files through
/// <see cref="ExtraGenesisDirectoryVariable"/>.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class GethGenesisDevnetTests
{
    /// <summary>Environment variable naming a directory of further genesis files to check.</summary>
    public const string ExtraGenesisDirectoryVariable = "NETHERMIND_GETH_GENESIS_DIR";

    private static readonly HashSet<string> GethConfigFields = new(
        typeof(GethGenesisConfigJson).GetProperties().Select(static p => p.Name), StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<TestCaseData> GenesisFiles()
    {
        foreach (string path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Specs", "GethGenesis"), "*.json").Order(StringComparer.Ordinal))
        {
            yield return new TestCaseData(path).SetArgDisplayNames(Path.GetFileName(path));
        }

        if (Environment.GetEnvironmentVariable(ExtraGenesisDirectoryVariable) is { Length: > 0 } extra)
        {
            string[] files = Directory.Exists(extra) ? Directory.GetFiles(extra, "*.json") : [];
            if (files.Length == 0)
            {
                // Surfaces a broken fetch instead of passing on nothing.
                yield return new TestCaseData(Path.Combine(extra, "<no genesis files>")).SetArgDisplayNames("upstream/<none>");
            }

            foreach (string path in files.Order(StringComparer.Ordinal))
            {
                yield return new TestCaseData(path).SetArgDisplayNames($"upstream/{Path.GetFileName(path)}");
            }
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
        Dictionary<string, string> forksByKey = GethGenesisParityTests.DiscoverGethTimeKeys().ToDictionary(static p => p.Value, static p => p.Key, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, NamedReleaseSpec> forkClasses = GethGenesisParityTests.DiscoverMainnetForks().ToDictionary(static f => f.GetType().Name, StringComparer.OrdinalIgnoreCase);

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
            if (config.TryGetProperty(timeKey, out JsonElement time)) times[entry.Name] = Math.Max(ReadUlong(time), genesisTimestamp);
        }

        // With one entry at a timestamp there is no ambiguity about which fork's parameters win there.
        foreach ((string key, ulong time) in times.Where(t => times.Values.Count(v => v == t.Value) == 1))
        {
            JsonElement entry = blobSchedule.GetProperty(key);
            IReleaseSpec spec = provider.GetSpec(new ForkActivation(1, time));
            Assert.That((spec.TargetBlobCount, spec.MaxBlobCount, spec.BlobBaseFeeUpdateFraction),
                Is.EqualTo((ReadUlong(entry.GetProperty("target")), ReadUlong(entry.GetProperty("max")), ReadUlong(entry.GetProperty("baseFeeUpdateFraction")))),
                $"blobSchedule.{key} at {time}");
        }
    }

    /// <summary>EIP flags <paramref name="fork"/> enables that <paramref name="spec"/> does not.</summary>
    private static List<string> MissingEips(NamedReleaseSpec fork, IReleaseSpec spec) =>
    [
        .. typeof(IReleaseSpec).GetProperties()
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
