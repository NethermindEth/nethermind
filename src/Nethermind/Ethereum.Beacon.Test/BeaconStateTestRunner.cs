// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum.Beacon.Test;

/// <summary>
/// Shared harness for consensus-spec state tests: enumerates test cases, loads
/// <c>pre/post.ssz_snappy</c> states, and applies the standard "mutate, then expect post-state or
/// expect an exception when no post-state exists" assertion.
/// </summary>
public static class BeaconStateTestRunner
{
    /// <summary>Enumerates <c>tests/mainnet/{fork}/{runner}/{handler}/{suite}/{case}</c> directories as test cases.</summary>
    public static IEnumerable<TestCaseData> EnumerateCases(string fork, string runner, string handler)
    {
        string handlerPath = BeaconConsensusTestLoader.GetTestPath(fork, runner, handler);
        if (!Directory.Exists(handlerPath))
            yield break;

        foreach (string suitePath in Directory.GetDirectories(handlerPath))
        {
            foreach (string casePath in Directory.GetDirectories(suitePath))
            {
                yield return new TestCaseData(casePath)
                    .SetName($"{fork}/{runner}/{handler}/{Path.GetFileName(suitePath)}/{Path.GetFileName(casePath)}");
            }
        }
    }

    /// <summary>Loads an <c>.ssz_snappy</c> file from the test case directory as a <see cref="BeaconStateFulu"/>.</summary>
    public static BeaconStateFulu LoadState(string casePath, string fileName = "pre.ssz_snappy")
    {
        BeaconStateFulu.Decode(BeaconConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, fileName)), out BeaconStateFulu state);
        return state;
    }

    /// <summary>Reads the case's <c>meta.yaml</c> (e.g. <c>bls_setting</c>) as scalar key/values; empty when absent.</summary>
    public static Dictionary<string, string> ReadMeta(string casePath)
    {
        Dictionary<string, string> meta = [];
        string metaPath = Path.Combine(casePath, "meta.yaml");
        if (!File.Exists(metaPath))
            return meta;

        using StreamReader reader = new(metaPath);
        YamlStream yaml = [];
        yaml.Load(reader);
        foreach (KeyValuePair<YamlNode, YamlNode> pair in (YamlMappingNode)yaml.Documents[0].RootNode)
        {
            meta[((YamlScalarNode)pair.Key).Value!] = ((YamlScalarNode)pair.Value).Value ?? string.Empty;
        }
        return meta;
    }

    /// <summary>
    /// Loads <c>pre.ssz_snappy</c>, applies <paramref name="transition"/>, and asserts the result
    /// equals <c>post.ssz_snappy</c> — or, when no post-state file exists, asserts that the
    /// transition throws.
    /// </summary>
    public static void RunStateTest(string casePath, Action<BeaconStateFulu> transition)
    {
        BeaconStateFulu pre = LoadState(casePath);
        string postPath = Path.Combine(casePath, "post.ssz_snappy");
        if (File.Exists(postPath))
        {
            transition(pre);
            AssertStatesEqual(LoadState(casePath, "post.ssz_snappy"), pre);
        }
        else
        {
            Assert.That(() => transition(pre), Throws.Exception, "the transition must fail: the test case has no post state");
        }
    }

    /// <summary>
    /// Runs a block-sequence case (sanity/blocks, finality, random): applies the case's
    /// <c>blocks_{i}.ssz_snappy</c> signed blocks to the pre-state through
    /// <see cref="StateTransition.Apply"/>, honoring the meta's <c>blocks_count</c> and
    /// <c>bls_setting</c> (2 disables signature verification).
    /// </summary>
    /// <remarks>
    /// The fixtures carry no <c>execution.yaml</c>, so the execution layer always accepts the
    /// payloads. The fixtures' epochs predate any BPO fork, so the Electra blob limit applies
    /// through <see cref="BeaconChainSpec.Mainnet"/>'s schedule fallback.
    /// </remarks>
    public static void RunBlocksTest(string casePath)
    {
        Dictionary<string, string> meta = ReadMeta(casePath);
        int blocksCount = int.Parse(meta["blocks_count"]);
        bool verifySignatures = !(meta.TryGetValue("bls_setting", out string? blsSetting) && blsSetting == "2");

        RunStateTest(casePath, state =>
        {
            EpochCache cache = new();
            PubkeyCache pubkeys = new();
            pubkeys.Build(state.Validators!);
            for (int i = 0; i < blocksCount; i++)
            {
                SignedBeaconBlock.Decode(BeaconConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, $"blocks_{i}.ssz_snappy")), out SignedBeaconBlock block);
                StateTransition.Apply(state, block, cache, pubkeys, new StubPayloadNotifier(true), BeaconChainSpec.Mainnet, verifySignatures: verifySignatures);
                // Deposits can grow the registry mid-sequence; later blocks may reference the new keys.
                if (state.Validators!.Length > pubkeys.Count)
                    pubkeys.Extend(state.Validators, pubkeys.Count);
            }
        });
    }

    /// <summary>An execution layer stub reporting every payload as <paramref name="valid"/>.</summary>
    public sealed class StubPayloadNotifier(bool valid) : INewPayloadNotifier
    {
        public bool NotifyNewPayload(BeaconBlockBody body) => valid;
    }

    /// <summary>
    /// Asserts hash-tree-root and byte-level equality of two states; on root mismatch the failure
    /// message names every differing top-level field.
    /// </summary>
    public static void AssertStatesEqual(BeaconStateFulu expected, BeaconStateFulu actual)
    {
        BeaconStateFulu.Merkleize(expected, out UInt256 expectedRoot);
        BeaconStateFulu.Merkleize(actual, out UInt256 actualRoot);
        if (expectedRoot != actualRoot)
        {
            Assert.Fail($"State root mismatch, differing fields: {string.Join(", ", DifferingFields(expected, actual))}");
        }
        Assert.That(BeaconStateFulu.Encode(actual), Is.EqualTo(BeaconStateFulu.Encode(expected)), "Re-encoded state does not match the expected post state");
    }

    /// <summary>Returns the names of top-level <see cref="BeaconStateFulu"/> fields whose values differ.</summary>
    public static List<string> DifferingFields(BeaconStateFulu expected, BeaconStateFulu actual)
    {
        List<string> diffs = [];
        void Check(string name, bool equal)
        {
            if (!equal)
                diffs.Add(name);
        }

        Check(nameof(expected.GenesisTime), expected.GenesisTime == actual.GenesisTime);
        Check(nameof(expected.GenesisValidatorsRoot), expected.GenesisValidatorsRoot == actual.GenesisValidatorsRoot);
        Check(nameof(expected.Slot), expected.Slot == actual.Slot);
        Check(nameof(expected.Fork), ContainerEquals(expected.Fork, actual.Fork));
        Check(nameof(expected.LatestBlockHeader), ContainerEquals(expected.LatestBlockHeader, actual.LatestBlockHeader));
        Check(nameof(expected.BlockRoots), HashesEqual(expected.BlockRoots, actual.BlockRoots));
        Check(nameof(expected.StateRoots), HashesEqual(expected.StateRoots, actual.StateRoots));
        Check(nameof(expected.HistoricalRoots), HashesEqual(expected.HistoricalRoots, actual.HistoricalRoots));
        Check(nameof(expected.Eth1Data), ContainerEquals(expected.Eth1Data, actual.Eth1Data));
        Check(nameof(expected.Eth1DataVotes), ContainersEqual(expected.Eth1DataVotes, actual.Eth1DataVotes));
        Check(nameof(expected.Eth1DepositIndex), expected.Eth1DepositIndex == actual.Eth1DepositIndex);
        Check(nameof(expected.Validators), ContainersEqual(expected.Validators, actual.Validators));
        Check(nameof(expected.Balances), SequenceEquals(expected.Balances, actual.Balances));
        Check(nameof(expected.RandaoMixes), HashesEqual(expected.RandaoMixes, actual.RandaoMixes));
        Check(nameof(expected.Slashings), SequenceEquals(expected.Slashings, actual.Slashings));
        Check(nameof(expected.PreviousEpochParticipation), SequenceEquals(expected.PreviousEpochParticipation, actual.PreviousEpochParticipation));
        Check(nameof(expected.CurrentEpochParticipation), SequenceEquals(expected.CurrentEpochParticipation, actual.CurrentEpochParticipation));
        Check(nameof(expected.JustificationBits), BitsEqual(expected.JustificationBits, actual.JustificationBits));
        Check(nameof(expected.PreviousJustifiedCheckpoint), ContainerEquals(expected.PreviousJustifiedCheckpoint, actual.PreviousJustifiedCheckpoint));
        Check(nameof(expected.CurrentJustifiedCheckpoint), ContainerEquals(expected.CurrentJustifiedCheckpoint, actual.CurrentJustifiedCheckpoint));
        Check(nameof(expected.FinalizedCheckpoint), ContainerEquals(expected.FinalizedCheckpoint, actual.FinalizedCheckpoint));
        Check(nameof(expected.InactivityScores), SequenceEquals(expected.InactivityScores, actual.InactivityScores));
        Check(nameof(expected.CurrentSyncCommittee), ContainerEquals(expected.CurrentSyncCommittee, actual.CurrentSyncCommittee));
        Check(nameof(expected.NextSyncCommittee), ContainerEquals(expected.NextSyncCommittee, actual.NextSyncCommittee));
        Check(nameof(expected.LatestExecutionPayloadHeader), ContainerEquals(expected.LatestExecutionPayloadHeader, actual.LatestExecutionPayloadHeader));
        Check(nameof(expected.NextWithdrawalIndex), expected.NextWithdrawalIndex == actual.NextWithdrawalIndex);
        Check(nameof(expected.NextWithdrawalValidatorIndex), expected.NextWithdrawalValidatorIndex == actual.NextWithdrawalValidatorIndex);
        Check(nameof(expected.HistoricalSummaries), ContainersEqual(expected.HistoricalSummaries, actual.HistoricalSummaries));
        Check(nameof(expected.DepositRequestsStartIndex), expected.DepositRequestsStartIndex == actual.DepositRequestsStartIndex);
        Check(nameof(expected.DepositBalanceToConsume), expected.DepositBalanceToConsume == actual.DepositBalanceToConsume);
        Check(nameof(expected.ExitBalanceToConsume), expected.ExitBalanceToConsume == actual.ExitBalanceToConsume);
        Check(nameof(expected.EarliestExitEpoch), expected.EarliestExitEpoch == actual.EarliestExitEpoch);
        Check(nameof(expected.ConsolidationBalanceToConsume), expected.ConsolidationBalanceToConsume == actual.ConsolidationBalanceToConsume);
        Check(nameof(expected.EarliestConsolidationEpoch), expected.EarliestConsolidationEpoch == actual.EarliestConsolidationEpoch);
        Check(nameof(expected.PendingDeposits), ContainersEqual(expected.PendingDeposits, actual.PendingDeposits));
        Check(nameof(expected.PendingPartialWithdrawals), ContainersEqual(expected.PendingPartialWithdrawals, actual.PendingPartialWithdrawals));
        Check(nameof(expected.PendingConsolidations), ContainersEqual(expected.PendingConsolidations, actual.PendingConsolidations));
        Check(nameof(expected.ProposerLookahead), SequenceEquals(expected.ProposerLookahead, actual.ProposerLookahead));

        return diffs;
    }

    private static bool ContainerEquals<T>(T? a, T? b) where T : class, ISszCodec<T> =>
        a is null || b is null ? ReferenceEquals(a, b) : T.Encode(a).AsSpan().SequenceEqual(T.Encode(b));

    private static bool ContainersEqual<T>(T[]? a, T[]? b) where T : class, ISszCodec<T>
    {
        if (a is null || b is null)
            return ReferenceEquals(a, b);
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!ContainerEquals(a[i], b[i]))
                return false;
        }
        return true;
    }

    private static bool HashesEqual(Hash256[]? a, Hash256[]? b)
    {
        if (a is null || b is null)
            return ReferenceEquals(a, b);
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    private static bool SequenceEquals<T>(T[]? a, T[]? b) where T : unmanaged, IEquatable<T> =>
        a is null || b is null ? ReferenceEquals(a, b) : a.AsSpan().SequenceEqual(b);

    private static bool BitsEqual(BitArray? a, BitArray? b)
    {
        if (a is null || b is null)
            return ReferenceEquals(a, b);
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }
}
