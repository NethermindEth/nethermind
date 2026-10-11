// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Autofac;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Test;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.Gnosis.Test;

public class GnosisPresetTests
{
    [OneTimeSetUp]
    public void SelectPreset()
    {
        using IContainer container = BeaconChainTestContainer.Builder(BlockchainIds.Gnosis).Build();
        Assert.That(container.Resolve<BeaconChainService>(), Is.Not.Null);
    }

    [Test]
    public void Preset_matches_published_gnosis_constants()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Presets.SlotsPerEpoch, Is.EqualTo(16));
            Assert.That(Presets.SecondsPerSlot, Is.EqualTo(5));
            Assert.That(Presets.BaseRewardFactor, Is.EqualTo(25));
            Assert.That(Presets.EpochsPerSyncCommitteePeriod, Is.EqualTo(512));
            Assert.That(Presets.MaxWithdrawalsPerPayload, Is.EqualTo(8));
            Assert.That(Presets.MaxValidatorsPerWithdrawalsSweep, Is.EqualTo(8192));
            Assert.That(Presets.MaxPendingPartialsPerWithdrawalsSweep, Is.EqualTo(6));
            Assert.That(Presets.ChurnLimitQuotient, Is.EqualTo(4096));
            Assert.That(Presets.MaxPerEpochActivationExitChurnLimit, Is.EqualTo(64_000_000_000UL));
            Assert.That(Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests, Is.EqualTo(16384));
        }
    }

    [Test]
    public void Chiado_shares_the_preset_but_ethereum_cannot_replace_it()
    {
        Assert.DoesNotThrow(() => BeaconPresetSelection.Initialize(BlockchainIds.Chiado));
        Assert.Throws<InvalidOperationException>(() => BeaconPresetSelection.Initialize(BlockchainIds.Mainnet));
    }

    [Test]
    public void Payload_rejects_a_ninth_withdrawal()
    {
        ExecutionPayload payload = new() { Withdrawals = new SszWithdrawal[8] };
        byte[] encoded = ExecutionPayload.Encode(payload);
        ExecutionPayload.Decode(encoded, out ExecutionPayload decoded);
        Assert.That(decoded.Withdrawals, Has.Length.EqualTo(8));
        payload.Withdrawals = new SszWithdrawal[9];
        Assert.Throws<InvalidDataException>(() => ExecutionPayload.Encode(payload));
    }

    [Test]
    public void Fulu_state_uses_the_gnosis_lookahead_and_eth1_vote_limits()
    {
        BeaconStateFulu state = new()
        {
            BlockRoots = Enumerable.Repeat(Hash256.Zero, 8192).ToArray(),
            StateRoots = Enumerable.Repeat(Hash256.Zero, 8192).ToArray(),
            RandaoMixes = Enumerable.Repeat(Hash256.Zero, 65536).ToArray(),
            Slashings = new ulong[8192],
            JustificationBits = new System.Collections.BitArray(4),
            CurrentSyncCommittee = new() { Pubkeys = new BlsPublicKey[512] },
            NextSyncCommittee = new() { Pubkeys = new BlsPublicKey[512] },
            ProposerLookahead = new ulong[32],
            LatestExecutionPayloadHeader = new(),
        };
        byte[] encoded = BeaconStateFulu.Encode(state);
        BeaconStateFulu.Decode(encoded, out BeaconStateFulu decoded);
        Assert.That(decoded.ProposerLookahead, Has.Length.EqualTo(32));
        BeaconStateFulu.Merkleize(state, out UInt256 root);
        BeaconStateFulu.Merkleize(decoded, out UInt256 decodedRoot);
        Assert.That(decodedRoot, Is.EqualTo(root));
        state.ProposerLookahead = new ulong[64];
        Assert.Throws<InvalidDataException>(() => BeaconStateFulu.Encode(state));
        state.ProposerLookahead = new ulong[32];
        state.Eth1DataVotes = Enumerable.Range(0, 1025).Select(_ => new Eth1Data()).ToArray();
        Assert.Throws<InvalidDataException>(() => BeaconStateFulu.Encode(state));
    }
}
