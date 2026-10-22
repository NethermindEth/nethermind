// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

/// <summary>
/// Fails when a fork or per-EIP activation can be scheduled through a Parity chainspec but not through an
/// EIP-7949 geth-style genesis, which is the only format devnets ship.
/// </summary>
/// <remarks>
/// Cases are discovered by reflection over the <c>Forks/*</c> classes, <see cref="GethGenesisConfigJson"/> and
/// <see cref="ChainSpecParamsJson"/>, so a new fork, EIP flag or chainspec field is covered without editing this file.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class GethGenesisParityTests
{
    private const ulong ForkTime = 0x10000;

    // Not a chain with a hard-coded provider, so nothing but the file decides the spec.
    private const ulong TestChainId = 0x3039;

    /// <summary>Named forks that have no geth label of their own, mapped to the forks whose labels together schedule them.</summary>
    private static readonly Dictionary<string, string[]> ForksScheduledByLabelCombination = new()
    {
        // Names the EIP set frames-devnet-1 runs; geth genesis generators only emit the per-prototype labels.
        // Its parent's eip8141PrototypeTime schedules frame transactions, these labels the EIPs on top.
        [nameof(FramesDevnet1)] = [nameof(Eip8250Prototype), nameof(Eip8272Prototype), nameof(Eip7906Prototype)],
    };

    /// <summary>
    /// Forks whose geth label is not a <c>blobSchedule</c> key, so a geth genesis cannot change blob parameters at them,
    /// unlike a Parity <c>blobSchedule</c> entry, which applies at any timestamp.
    /// </summary>
    private static readonly HashSet<string> ForksWithoutBlobScheduleKey =
    [
        // Prototypes and devnet forks riding on Amsterdam's blob parameters; devnets key blob changes by the fork they run on.
        nameof(Eip8141Prototype),
        nameof(Eip8250Prototype),
        nameof(Eip8272Prototype),
        nameof(Eip7906Prototype),
        nameof(Eip4758Prototype),
        nameof(BogotaDevnet2),
    ];

    /// <summary>Named forks no geth label schedules on this deployment branch, with the reason.</summary>
    private static readonly Dictionary<string, string> ForksWithoutGethLabel = new()
    {
        [nameof(Bogota)] = "bogotaTime schedules BogotaDevnet2 on the bogota-devnet-2 branch",
    };

    /// <summary>Parity <c>*TransitionTimestamp</c> fields a geth genesis cannot set, with the reason.</summary>
    private static readonly Dictionary<string, string> ParityOnlyTransitionTimestamps = new()
    {
        // Parameters of chains that only ship Parity chainspecs.
        [nameof(ChainSpecParamsJson.Eip4844FeeCollectorTransitionTimestamp)] = "Gnosis blob fee collector; Gnosis and Chiado ship Parity chainspecs",
        [nameof(ChainSpecParamsJson.MaxCodeSizeTransitionTimestamp)] = "Gnosis and Chiado code-size limit; L1 genesis gets EIP-170 from spuriousDragon and EIP-7954 from amsterdamTime",
        [nameof(ChainSpecParamsJson.Rip7212TransitionTimestamp)] = "rollup precompile; L1 gets P-256 verification from EIP-7951 in osakaTime",
        [nameof(ChainSpecParamsJson.Eip7805TransitionTimestamp)] = "bogotaTime schedules BogotaDevnet2 on the bogota-devnet-2 branch",

        // Standalone EIPs in no named fork. EIP-7949 has no per-EIP fields, so a geth-style devnet cannot schedule them.
        // TODO: add each to a fork, or give it a *Prototype fork and geth label as EIP-8141 has, once a devnet needs it.
        [nameof(ChainSpecParamsJson.Eip3298TransitionTimestamp)] = StandaloneEip,
        [nameof(ChainSpecParamsJson.Eip5920TransitionTimestamp)] = StandaloneEip,
        [nameof(ChainSpecParamsJson.Eip7668TransitionTimestamp)] = StandaloneEip,
        [nameof(ChainSpecParamsJson.Eip7709TransitionTimestamp)] = StandaloneEip,
        [nameof(ChainSpecParamsJson.Eip7979TransitionTimestamp)] = StandaloneEip,
        [nameof(ChainSpecParamsJson.Eip8116TransitionTimestamp)] = StandaloneEip,
        [nameof(ChainSpecParamsJson.Eip8131TransitionTimestamp)] = StandaloneEip,
        [nameof(ChainSpecParamsJson.Eip8279TransitionTimestamp)] = StandaloneEip,
        [nameof(ChainSpecParamsJson.Eip8360TransitionTimestamp)] = StandaloneEip,
    };

    private const string StandaloneEip = "standalone EIP, not in a named fork";

    /// <summary>Geth config fields that are not fork labels and have no same-named Parity <c>params</c> field, with the reason.</summary>
    private static readonly Dictionary<string, string> GethOnlyConfigFields = new()
    {
        [nameof(GethGenesisConfigJson.TerminalTotalDifficultyPassed)] = "informational in geth; Nethermind reads terminalTotalDifficulty only",
        [nameof(GethGenesisConfigJson.Eip150Block)] = "Parity's eip150Transition",
        [nameof(GethGenesisConfigJson.Eip155Block)] = "Parity's eip155Transition",
        [nameof(GethGenesisConfigJson.Eip158Block)] = "Parity's eip161abcTransition and eip161dTransition",
    };

    /// <summary>
    /// Proof-of-work members: the classes keep mainnet's schedule, a geth genesis gets the Ethash engine's and the Parity
    /// test chainspec runs NethDev. None of them applies after the merge.
    /// </summary>
    private static readonly HashSet<string> PowOnlySpecMembers =
    [
        nameof(IReleaseSpec.BlockReward),
        nameof(IReleaseSpec.DifficultyBombDelay),
        nameof(IReleaseSpec.DifficultyBoundDivisor),
    ];

    /// <summary>
    /// <see cref="IReleaseSpec"/> members a chainspec cannot match against a named fork class, because the class carries
    /// mainnet's value.
    /// </summary>
    private static readonly HashSet<string> MainnetOnlySpecMembers =
    [
        nameof(IReleaseSpec.Name),
        .. PowOnlySpecMembers,
        // Mainnet activation points baked into the classes for RLP decoding.
        nameof(IReleaseSpec.Eip1559TransitionBlock),
        nameof(IReleaseSpec.WithdrawalTimestamp),
        nameof(IReleaseSpec.Eip4844TransitionTimestamp),
    ];

    internal static readonly Lazy<NamedReleaseSpec[]> MainnetForks = new(DiscoverMainnetForks);
    internal static readonly Lazy<Dictionary<string, string>> GethTimeKeysByFork = new(DiscoverGethTimeKeys);

    /// <summary>Ancestors a fork's geth label schedules together with the fork, keyed by fork class name.</summary>
    private static readonly Lazy<Dictionary<string, HashSet<string>>> AncestorsScheduledByLabel = new(DiscoverAncestorsScheduledByLabel);

    public static IEnumerable<TestCaseData> TimestampForkCases() =>
        MainnetForks.Value.Where(static f => IsTimestampFork(f) && !ForksWithoutGethLabel.ContainsKey(f.GetType().Name)).Select(f => new TestCaseData(f).SetArgDisplayNames(f.GetType().Name));

    /// <summary>
    /// A geth genesis that schedules <paramref name="fork"/> at <see cref="ForkTime"/>, its ancestors at genesis, gives at
    /// <see cref="ForkTime"/> the fork class itself and one second earlier its parent.
    /// </summary>
    [TestCaseSource(nameof(TimestampForkCases))]
    public void Geth_fork_label_schedules_the_named_fork(NamedReleaseSpec fork)
    {
        ChainSpecBasedSpecProvider provider = new(LoadGeth(BuildGethGenesis(fork)));

        IReleaseSpec atFork = provider.GetSpec(new ForkActivation(1, ForkTime));
        IReleaseSpec beforeFork = provider.GetSpec(new ForkActivation(1, ForkTime - 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DiffAgainstFork(atFork, fork), Is.Empty, $"geth genesis at {fork.Name} differs from the {fork.GetType().Name} class");
            // Paris is the last fork before Shanghai, but a chain merged at genesis is not the Paris class.
            NamedReleaseSpec parent = Ancestry(fork.Parent!).First(f => !IsScheduledByLabelOf(fork, f));
            if (IsTimestampFork(parent))
            {
                Assert.That(DiffAgainstFork(beforeFork, parent), Is.Empty, $"geth genesis just before {fork.Name} differs from the {parent.GetType().Name} class");
            }
        }
    }

    /// <summary>
    /// A Parity chainspec that schedules <paramref name="fork"/> through its per-EIP <c>*TransitionTimestamp</c> fields
    /// and a geth genesis that schedules it through its label give the same spec, compared over every
    /// <see cref="IReleaseSpec"/> member.
    /// </summary>
    [TestCaseSource(nameof(TimestampForkCases))]
    public void Parity_per_eip_timestamps_and_geth_fork_label_give_the_same_spec(NamedReleaseSpec fork)
    {
        ChainSpecBasedSpecProvider geth = new(LoadGeth(BuildGethGenesis(fork)));
        ChainSpecBasedSpecProvider parity = new(LoadParity(BuildParityChainSpec(fork)));

        using (Assert.EnterMultipleScope())
        {
            foreach (ulong timestamp in (ulong[])[0, ForkTime - 1, ForkTime])
            {
                ForkActivation activation = new(1, timestamp);
                Assert.That(Diff(geth.GetSpec(activation), parity.GetSpec(activation), PowOnlySpecMembers),
                    Is.Empty, $"{fork.Name} at timestamp {timestamp}: geth (expected) vs Parity (actual)");
            }
        }
    }

    public static IEnumerable<TestCaseData> BlobForkCases() =>
        MainnetForks.Value
            .Where(static f => IsTimestampFork(f) && f.IsEip4844Enabled && GethTimeKeysByFork.Value.ContainsKey(f.GetType().Name))
            .Select(static f => new TestCaseData(f, !ForksWithoutBlobScheduleKey.Contains(f.GetType().Name)).SetArgDisplayNames(f.GetType().Name));

    /// <summary>
    /// A geth <c>blobSchedule</c> entry keyed by any fork's label takes effect at that fork, as a Parity
    /// <c>blobSchedule</c> entry does at any timestamp.
    /// </summary>
    [TestCaseSource(nameof(BlobForkCases))]
    public void Geth_blob_schedule_entry_takes_effect_at_its_fork(NamedReleaseSpec fork, bool expectHonoured)
    {
        string key = StripTime(GethTimeKeysByFork.Value[fork.GetType().Name]);

        Assert.That(IsBlobScheduleKeyHonoured(fork), Is.EqualTo(expectHonoured), expectHonoured
            ? $"blobSchedule.{key} was ignored; map {fork.GetType().Name} in GethGenesisLoader, or list it in {nameof(ForksWithoutBlobScheduleKey)} with a reason"
            : $"blobSchedule.{key} is honoured now; remove {fork.GetType().Name} from {nameof(ForksWithoutBlobScheduleKey)}");
    }

    [Test]
    public void Bogota_blob_schedule_wins_over_bpo5_at_the_same_timestamp()
    {
        JsonNode genesis = JsonNode.Parse(BuildGethGenesis(BogotaDevnet2.Instance, ("bogota", 7, 11, 222)))!;
        genesis["config"]!["bpo5Time"] = ForkTime;
        genesis["config"]!["blobSchedule"]!["bpo5"] = JsonNode.Parse("""{ "target": 3, "max": 6, "baseFeeUpdateFraction": 111 }""");
        IReleaseSpec spec = new ChainSpecBasedSpecProvider(LoadGeth(genesis.ToJsonString())).GetSpec(new ForkActivation(1, ForkTime));

        Assert.That((spec.TargetBlobCount, spec.MaxBlobCount, spec.BlobBaseFeeUpdateFraction),
            Is.EqualTo((7UL, 11UL, 222UL)));
    }

    /// <summary>Whether a <c>blobSchedule</c> entry keyed by <paramref name="fork"/>'s geth label changes the spec at that fork.</summary>
    internal static bool IsBlobScheduleKeyHonoured(NamedReleaseSpec fork)
    {
        const ulong target = 7, max = 11, fraction = 1234567;
        string genesis = BuildGethGenesis(fork, (StripTime(GethTimeKeysByFork.Value[fork.GetType().Name]), target, max, fraction));
        IReleaseSpec atFork = new ChainSpecBasedSpecProvider(LoadGeth(genesis)).GetSpec(new ForkActivation(1, ForkTime));
        return (atFork.TargetBlobCount, atFork.MaxBlobCount, atFork.BlobBaseFeeUpdateFraction) == (target, max, fraction);
    }

    [Test]
    public void Every_parity_transition_timestamp_is_reachable_from_geth_genesis()
    {
        // Every geth timestamp label at once; whatever ends up unset is unreachable from a geth genesis.
        string labels = string.Join(", ", GethTimeKeysByFork.Value.Values.Select(k => $"\"{k}\": {ForkTime}"));
        ChainParameters parameters = LoadGeth(BuildGethGenesisJson(labels, blobSchedule: "")).Parameters;

        List<string> unreachable = [];
        List<string> staleAllowlist = [];
        foreach (PropertyInfo property in typeof(ChainSpecParamsJson).GetProperties().Where(static p => p.Name.EndsWith("TransitionTimestamp", StringComparison.Ordinal)))
        {
            PropertyInfo target = typeof(ChainParameters).GetProperty(property.Name)
                ?? throw new InvalidOperationException($"{nameof(ChainParameters)} has no {property.Name}");
            bool reachable = target.GetValue(parameters) is not null;
            bool allowlisted = ParityOnlyTransitionTimestamps.ContainsKey(property.Name);

            if (!reachable && !allowlisted) unreachable.Add(property.Name);
            if (reachable && allowlisted) staleAllowlist.Add(property.Name);
        }

        string[] missing = [.. ParityOnlyTransitionTimestamps.Keys.Where(static k => typeof(ChainSpecParamsJson).GetProperty(k) is null)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unreachable, Is.Empty,
                "These Parity params cannot be set from a geth genesis. Add the EIP to a named fork (or a *Prototype fork with a " +
                $"{nameof(GethGenesisConfigJson)} *Time label), or allowlist it in {nameof(ParityOnlyTransitionTimestamps)} with a reason");
            Assert.That(staleAllowlist, Is.Empty, $"reachable from geth genesis now; remove from {nameof(ParityOnlyTransitionTimestamps)}");
            Assert.That(missing, Is.Empty, $"no longer a {nameof(ChainSpecParamsJson)} field; remove from {nameof(ParityOnlyTransitionTimestamps)}");
        }
    }

    [Test]
    public void Every_geth_config_field_has_a_parity_counterpart()
    {
        List<string> gethOnly = [];
        foreach (PropertyInfo property in typeof(GethGenesisConfigJson).GetProperties())
        {
            if (IsForkLabel(property) || GethOnlyConfigFields.ContainsKey(property.Name)) continue;
            if (typeof(ChainSpecParamsJson).GetProperty(property.Name) is null) gethOnly.Add(property.Name);
        }

        string[] stale = [.. GethOnlyConfigFields.Keys.Where(static k => typeof(GethGenesisConfigJson).GetProperty(k) is null)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gethOnly, Is.Empty, $"add a same-named {nameof(ChainSpecParamsJson)} field, or allowlist it in {nameof(GethOnlyConfigFields)} with a reason");
            Assert.That(stale, Is.Empty, $"no longer a {nameof(GethGenesisConfigJson)} field; remove from {nameof(GethOnlyConfigFields)}");
        }
    }

    [Test]
    public void Allowlisted_label_combinations_name_unlabelled_forks()
    {
        using (Assert.EnterMultipleScope())
        {
            foreach ((string fork, string[] parts) in ForksScheduledByLabelCombination)
            {
                Assert.That(MainnetForks.Value.Select(static f => f.GetType().Name), Does.Contain(fork), $"{fork} is no longer a fork class");
                Assert.That(GethTimeKeysByFork.Value.ContainsKey(fork), Is.False, $"{fork} has its own geth label now; remove it from {nameof(ForksScheduledByLabelCombination)}");
                Assert.That(parts.Where(p => !GethTimeKeysByFork.Value.ContainsKey(p)), Is.Empty, $"{fork}: parts without a geth label");
            }
        }
    }

    private static NamedReleaseSpec[] DiscoverMainnetForks() =>
    [
        .. typeof(NamedReleaseSpec).Assembly.GetTypes()
            .Where(static t => t.IsSubclassOf(typeof(NamedReleaseSpec)) && t is { IsAbstract: false, IsGenericType: false } && t.Namespace == typeof(NamedReleaseSpec).Namespace)
            .Select(static t => (NamedReleaseSpec)t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!.GetValue(null)!)
            .OrderBy(static f => Ancestry(f).Count())
            .ThenBy(static f => f.GetType().Name, StringComparer.Ordinal)
    ];

    /// <summary>Maps each fork class name to the geth config key of the <c>*Time</c> property that schedules it.</summary>
    private static Dictionary<string, string> DiscoverGethTimeKeys()
    {
        Dictionary<string, string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyInfo property in typeof(GethGenesisConfigJson).GetProperties().Where(static p => IsForkLabel(p) && p.Name.EndsWith("Time", StringComparison.Ordinal)))
        {
            // Read the routed key back instead of trusting the property name: a property may label a differently named fork.
            keys.Add(NewestFork(RoutedForks(property)), ToCamelCase(property.Name));
        }

        return keys;
    }

    private static Dictionary<string, HashSet<string>> DiscoverAncestorsScheduledByLabel()
    {
        Dictionary<string, HashSet<string>> ancestors = new(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyInfo property in typeof(GethGenesisConfigJson).GetProperties().Where(static p => IsForkLabel(p) && p.Name.EndsWith("Time", StringComparison.Ordinal)))
        {
            string[] forks = RoutedForks(property);
            string newest = NewestFork(forks);
            ancestors[newest] = new HashSet<string>(forks.Where(f => f != newest), StringComparer.OrdinalIgnoreCase);
        }

        return ancestors;
    }

    /// <summary>Fork keys <paramref name="property"/> writes, read back instead of trusting the property name.</summary>
    private static string[] RoutedForks(PropertyInfo property)
    {
        GethGenesisConfigJson probe = new();
        property.SetValue(probe, (ulong?)1);
        return [.. ((IHasNamedForks)probe).NamedForkTimestamps!.Keys];
    }

    private static string NewestFork(string[] forks) =>
        forks.MaxBy(static name => MainnetForks.Value.FirstOrDefault(f => string.Equals(f.GetType().Name, name, StringComparison.OrdinalIgnoreCase)) is { } f ? Ancestry(f).Count() : 0)!;

    private static bool IsScheduledByLabelOf(NamedReleaseSpec fork, NamedReleaseSpec ancestor) =>
        AncestorsScheduledByLabel.Value.TryGetValue(fork.GetType().Name, out HashSet<string>? covered) && covered.Contains(ancestor.GetType().Name);

    internal static bool IsTimestampFork(NamedReleaseSpec fork) => Ancestry(fork).Contains(Shanghai.Instance);

    /// <summary><paramref name="fork"/> and its ancestors, newest first.</summary>
    internal static IEnumerable<NamedReleaseSpec> Ancestry(NamedReleaseSpec fork)
    {
        for (NamedReleaseSpec? f = fork; f is not null; f = f.Parent) yield return f;
    }

    /// <summary>Geth <c>*Time</c> keys that schedule <paramref name="fork"/>'s own changes.</summary>
    private static string[] GethKeysFor(NamedReleaseSpec fork)
    {
        string name = fork.GetType().Name;
        if (GethTimeKeysByFork.Value.TryGetValue(name, out string? key)) return [key];
        if (ForksScheduledByLabelCombination.TryGetValue(name, out string[]? parts)) return [.. parts.Select(p => GethTimeKeysByFork.Value[p])];

        Assert.Fail($"{name} can be scheduled from a Parity chainspec but no {nameof(GethGenesisConfigJson)} *Time property schedules it. " +
            $"Add a {char.ToUpperInvariant(name[0])}{name[1..]}Time property (or list it in {nameof(ForksScheduledByLabelCombination)} if other labels compose it).");
        return [];
    }

    /// <summary>
    /// Timestamp forks from Shanghai to <paramref name="fork"/>, oldest first, each with its activation: genesis for
    /// ancestors, <see cref="ForkTime"/> for the fork itself.
    /// </summary>
    private static IEnumerable<(NamedReleaseSpec Fork, ulong Time)> Schedule(NamedReleaseSpec fork) =>
        Ancestry(fork).Where(IsTimestampFork).Reverse().Select(f => (f, f == fork || IsScheduledByLabelOf(fork, f) ? ForkTime : 0UL));

    private static bool ChangesBlobParams(NamedReleaseSpec fork) =>
        fork.Parent is { } parent
        && (fork.TargetBlobCount, fork.MaxBlobCount, fork.BlobBaseFeeUpdateFraction) != (parent.TargetBlobCount, parent.MaxBlobCount, parent.BlobBaseFeeUpdateFraction);

    /// <param name="blobOverride">A <c>blobSchedule</c> entry replacing whatever the fork classes would give that key.</param>
    internal static string BuildGethGenesis(NamedReleaseSpec fork, (string Key, ulong Target, ulong Max, ulong Fraction)? blobOverride = null)
    {
        List<string> labels = [];
        Dictionary<string, string> blobEntries = [];
        foreach ((NamedReleaseSpec f, ulong time) in Schedule(fork).Where(s => !IsScheduledByLabelOf(fork, s.Fork)))
        {
            string[] keys = GethKeysFor(f);
            labels.AddRange(keys.Select(k => $"\"{k}\": {time}"));
            if (ChangesBlobParams(f))
            {
                string key = StripTime(keys.Single());
                blobEntries[key] = BlobEntry(key, f.TargetBlobCount, f.MaxBlobCount, f.BlobBaseFeeUpdateFraction);
            }
        }

        if (blobOverride is { } o) blobEntries[o.Key] = BlobEntry(o.Key, o.Target, o.Max, o.Fraction);

        return BuildGethGenesisJson(string.Join(", ", labels), string.Join(", ", blobEntries.Values));

        static string BlobEntry(string key, ulong target, ulong max, ulong fraction) =>
            $$"""
            "{{key}}": { "target": {{target}}, "max": {{max}}, "baseFeeUpdateFraction": {{fraction}} }
            """;
    }

    private static string BuildGethGenesisJson(string timeLabels, string blobSchedule) =>
        $$"""
        {
          "config": {
            "chainId": {{TestChainId}},
            "homesteadBlock": 0, "eip150Block": 0, "eip155Block": 0, "eip158Block": 0,
            "byzantiumBlock": 0, "constantinopleBlock": 0, "petersburgBlock": 0, "istanbulBlock": 0,
            "berlinBlock": 0, "londonBlock": 0, "mergeNetsplitBlock": 0,
            "terminalTotalDifficulty": 0,
            "depositContractAddress": "{{Eip6110Constants.MainnetDepositContractAddress}}",
            "blobSchedule": { {{blobSchedule}} },
            {{timeLabels}}
          },
          "timestamp": "0x0",
          "difficulty": "0x0",
          "gasLimit": "0x3938700",
          "alloc": {}
        }
        """;

    /// <summary>
    /// A Parity chainspec scheduling <paramref name="fork"/> through per-EIP fields, derived from the fork classes rather
    /// than <see cref="HardforkLabels"/> so it stays independent of what the geth side expands.
    /// </summary>
    private static string BuildParityChainSpec(NamedReleaseSpec fork)
    {
        List<string> fields = [];

        // Pre-Shanghai forks through their block labels: frozen history, not under test here.
        fields.AddRange(HardforkLabels.All.Where(static l => l.Kind == HardforkLabelKind.Block).Select(static l => $"\"{ToCamelCase(l.LabelName)}\": \"0x0\""));

        List<string> blobEntries = [];
        foreach ((NamedReleaseSpec f, ulong time) in Schedule(fork))
        {
            foreach (int eip in NewlyEnabledEips(f))
            {
                string field = $"Eip{eip}TransitionTimestamp";
                if (typeof(ChainSpecParamsJson).GetProperty(field) is not null) fields.Add($"\"{ToCamelCase(field)}\": \"0x{time:x}\"");
            }

            if (ChangesBlobParams(f))
            {
                blobEntries.Add($$"""{ "timestamp": "0x{{time:x}}", "target": {{f.TargetBlobCount}}, "max": {{f.MaxBlobCount}}, "baseFeeUpdateFraction": "0x{{f.BlobBaseFeeUpdateFraction:x}}" }""");
            }
        }

        return $$"""
            {
              "name": "parity",
              "engine": { "NethDev": {} },
              "params": {
                "chainID": "0x{{TestChainId:x}}",
                "networkID": "0x{{TestChainId:x}}",
                "maxCodeSize": "0x6000",
                "maxCodeSizeTransition": "0x0",
                "terminalTotalDifficulty": "0x0",
                "depositContractAddress": "{{Eip6110Constants.MainnetDepositContractAddress}}",
                "blobSchedule": [ {{string.Join(", ", blobEntries)}} ],
                {{string.Join(",\n", fields)}}
              },
              "genesis": { "seal": { "ethereum": { "nonce": "0x0", "mixHash": "0x0000000000000000000000000000000000000000000000000000000000000000" } }, "difficulty": "0x0", "author": "0x0000000000000000000000000000000000000000", "timestamp": "0x0", "parentHash": "0x0000000000000000000000000000000000000000000000000000000000000000", "extraData": "0x", "gasLimit": "0x3938700" },
              "accounts": {}
            }
            """;
    }

    private static IEnumerable<int> NewlyEnabledEips(NamedReleaseSpec fork) =>
        typeof(ReleaseSpec).GetProperties()
            .Where(static p => p.PropertyType == typeof(bool) && p.Name.StartsWith("IsEip", StringComparison.Ordinal) && p.Name.EndsWith("Enabled", StringComparison.Ordinal))
            .Where(p => (bool)p.GetValue(fork)! && !(bool)p.GetValue(fork.Parent)!)
            .Select(static p => int.TryParse(p.Name["IsEip".Length..^"Enabled".Length], out int eip) ? eip : -1)
            .Where(static eip => eip > 0);

    internal static ChainSpec LoadGeth(string json)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        return new GethGenesisLoader(new EthereumJsonSerializer()).Load(stream);
    }

    private static ChainSpec LoadParity(string json)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        return new ChainSpecLoader(new EthereumJsonSerializer(), LimboLogs.Instance).Load(stream);
    }

    /// <summary>Differences between a chainspec-built spec and a fork class, skipping members the class fixes to mainnet.</summary>
    internal static List<string> DiffAgainstFork(IReleaseSpec actual, NamedReleaseSpec fork) => Diff(fork, actual, MainnetOnlySpecMembers);

    /// <summary>Compares every <see cref="IReleaseSpec"/> member, inherited interfaces included.</summary>
    private static List<string> Diff(IReleaseSpec expected, IReleaseSpec actual, IReadOnlyCollection<string> skip)
    {
        List<string> differences = [];
        foreach (PropertyInfo property in SpecProperties)
        {
            if (skip.Contains(property.Name)) continue;

            object? e = property.GetValue(expected);
            object? a = property.GetValue(actual);
            bool equal = (e, a) switch
            {
                (IEnumerable ei and not string, IEnumerable ai) => ei.Cast<object>().ToHashSet().SetEquals(ai.Cast<object>()),
                // Strategy objects without value equality (e.g. the base-fee calculator) match when they are the same kind.
                ({ } eo, { } ao) when !HasValueEquality(eo.GetType()) => eo.GetType() == ao.GetType(),
                _ => Equals(e, a),
            };
            if (!equal) differences.Add($"{property.Name}: expected {e}, actual {a}");
        }

        // Derived predicates, including the fork markers (IsPragueEnabled ... IsBogotaEnabled) the engine API validates
        // payload versions against.
        foreach (MethodInfo predicate in DerivedPredicates)
        {
            object? e = predicate.Invoke(null, [expected]);
            object? a = predicate.Invoke(null, [actual]);
            if (!Equals(e, a)) differences.Add($"{predicate.Name["get_".Length..]}: expected {e}, actual {a}");
        }

        return differences;
    }

    private static bool HasValueEquality(Type type) =>
        type.IsValueType || type.GetMethod(nameof(Equals), [typeof(object)])!.DeclaringType != typeof(object);

    internal static readonly PropertyInfo[] SpecProperties =
        [.. typeof(IReleaseSpec).GetInterfaces().Prepend(typeof(IReleaseSpec)).SelectMany(static i => i.GetProperties())];

    private static readonly MethodInfo[] DerivedPredicates =
    [
        .. typeof(IReleaseSpecExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(static m => m.Name.StartsWith("get_", StringComparison.Ordinal) && m.ReturnType == typeof(bool)
                && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(IReleaseSpec))
    ];

    private static bool IsForkLabel(PropertyInfo property) =>
        property.PropertyType == typeof(ulong?)
        && (property.Name.EndsWith("Time", StringComparison.Ordinal) || property.Name.EndsWith("Block", StringComparison.Ordinal))
        && !GethOnlyConfigFields.ContainsKey(property.Name);

    internal static string StripTime(string key) => key[..^"Time".Length];

    private static string ToCamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
