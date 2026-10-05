// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;
using static Ethereum.ConsensusSpec.Test.OperationVectorHandlers;
using FuluStateTransition = Nethermind.BeaconChain.StateTransition.StateTransition;

namespace Ethereum.ConsensusSpec.Test;

/// <remarks>Mainnet only; requires NETHERMIND_CONSENSUS_SPEC_MAINNET=1.</remarks>
[TestFixture]
public class BlockSignatureBatchVectorTests
{
    private readonly record struct Outcome(string? Exception, string? Message, Hash256? Root)
    {
        public bool Accepted => Exception is null;

        public override string ToString() => Accepted ? $"accepted with root {Root}" : $"{Exception}: {Message}";
    }

    [TestCaseSource(nameof(SanityBlockCases))]
    public void Sanity_blocks_are_accepted_and_refused_alike(SanityCase testCase)
    {
        Outcome serial = RunBlocks(testCase, batched: false);
        Outcome batched = RunBlocks(testCase, batched: true);
        AssertAlike(testCase.CasePath, serial, batched);
    }

    [TestCaseSource(nameof(OperationCases))]
    public void Signature_operations_are_accepted_and_refused_alike(OperationCase testCase)
    {
        Outcome serial = RunOperation(testCase, batched: false);
        Outcome batched = RunOperation(testCase, batched: true);
        AssertAlike(testCase.CasePath, serial, batched);
    }
    [Test]
    public void Every_fork_and_signature_operation_has_vectors()
    {
        if (!ConsensusSpecArchive.MainnetEnabled)
            Assert.Ignore("Set NETHERMIND_CONSENSUS_SPEC_MAINNET=1 to enumerate the mainnet vectors this differential runs over.");

        List<SanityCase> blocks = [.. SanityBlockCases().Select(static data => (SanityCase)data.Arguments[0]!)];
        List<OperationCase> operations = [.. OperationCases().Select(static data => (OperationCase)data.Arguments[0]!)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Select(static c => c.Fork).Distinct(), Is.EquivalentTo(ConsensusSpecArchive.StateTransitionForks), "sanity/blocks forks");
            foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
            {
                IEnumerable<string> table = fork == "gloas" ? GloasOperations.Keys : FuluOperations.Keys;
                Assert.That(operations.Where(c => c.Fork == fork).Select(static c => c.OperationName).Distinct(), Is.EquivalentTo(table), $"{fork} operations");
            }
        }
    }

    private static void AssertAlike(string casePath, Outcome serial, Outcome batched)
    {
        bool expectAccepted = File.Exists(Path.Combine(casePath, "post.ssz_snappy"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batched, Is.EqualTo(serial), "batched outcome against the serial one");
            Assert.That(serial.Accepted, Is.EqualTo(expectAccepted), $"serial outcome against the vector: {serial}");
        }
    }

    private static Outcome RunBlocks(SanityCase testCase, bool batched) => FuluDriverSupport.RequireForkDriver(testCase.Fork) switch
    {
        ForkDriver<BeaconStateFulu> fulu => RunBlocks(testCase, fulu, batched, (state, ssz, spec, cache, pubkeys, verify) =>
        {
            SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock signedBlock);
            BeaconBlock block = signedBlock.Message!;
            // The Electra driver advances slots with its own lookahead refill, so only Fulu can run the production transition.
            if (batched && fulu.Fork == "fulu")
            {
                FuluStateTransition.Apply(state, signedBlock, cache, pubkeys, new AcceptingNotifier(), spec, validateResult: false, verify);
                return block.StateRoot!;
            }

            fulu.ProcessSlots(state, block.Slot, cache);
            if (verify && !SignatureSets.VerifyProposerSignature(state, signedBlock, pubkeys))
                throw new ProposerSignatureException($"Invalid proposer signature for the block at slot {block.Slot}");
            // The Electra driver runs under the Electra blob limit, as its own ApplyBlock does.
            ulong maxBlobs = fulu.Fork == "electra"
                ? spec.MaxBlobsPerBlockElectra
                : spec.GetBlobParameters(state.GetCurrentEpoch())?.MaxBlobsPerBlock ?? spec.MaxBlobsPerBlockElectra;
            if (batched)
                BlockProcessing.ProcessBlock(state, block, cache, pubkeys, new AcceptingNotifier(), maxBlobs, verify);
            else
                BlockProcessing.ProcessBlock(state, block, cache, pubkeys, new AcceptingNotifier(), maxBlobs, verify, batch: null);
            return block.StateRoot!;
        }),
        ForkDriver<BeaconStateGloas> gloas => RunBlocks(testCase, gloas, batched, (state, ssz, spec, cache, pubkeys, verify) =>
        {
            SignedBeaconBlockGloas.Decode(ssz, out SignedBeaconBlockGloas signedBlock);
            BeaconBlockGloas block = signedBlock.Message!;
            gloas.ProcessSlots(state, block.Slot, cache);
            if (verify && !GloasBlockProcessing.VerifyProposerSignature(state, signedBlock, pubkeys))
                throw new ProposerSignatureException($"Invalid proposer signature for the block at slot {block.Slot}");
            if (batched)
                GloasBlockProcessing.ProcessBlock(state, block, cache, pubkeys, new AcceptingNotifier(), spec, verify);
            else
                GloasBlockProcessing.ProcessBlock(state, block, cache, pubkeys, new AcceptingNotifier(), spec, verify, batch: null);
            return block.StateRoot!;
        }),
        ForkDriver other => throw new InvalidOperationException($"fork '{other.Fork}' has no state type this differential knows"),
    };

    private delegate Hash256 ApplyBlock<in TState>(TState state, byte[] ssz, BeaconChainSpec spec, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures);

    private static Outcome RunBlocks<TState>(SanityCase testCase, ForkDriver<TState> driver, bool batched, ApplyBlock<TState> apply) where TState : class
    {
        TState state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
        EpochCache cache = driver.NewCache();
        PubkeyCache pubkeys = FuluDriverSupport.BuildPubkeyCache(driver.ValidatorsOf(state));
        bool verifySignatures = FuluDriverSupport.ShouldVerifySignatures(testCase.CasePath);
        BeaconChainSpec spec = FuluDriverSupport.CaseSpec(testCase.CasePath);
        int blocksCount = int.Parse(FuluDriverSupport.ParseFlowMap(Path.Combine(testCase.CasePath, "meta.yaml"))["blocks_count"]);

        try
        {
            for (int i = 0; i < blocksCount; i++)
            {
                byte[] ssz = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(testCase.CasePath, $"blocks_{i}.ssz_snappy"));
                Hash256 claimedRoot = apply(state, ssz, spec, cache, pubkeys, verifySignatures);
                if (claimedRoot != driver.StateRoot(state))
                    throw new BeaconStateException($"Block state root {claimedRoot} does not match the post-state root");
            }
        }
        catch (Exception e)
        {
            return new Outcome(e.GetType().FullName, e.Message, null);
        }

        return new Outcome(null, null, driver.StateRoot(state));
    }

    private static Outcome RunOperation(OperationCase testCase, bool batched) => FuluDriverSupport.RequireForkDriver(testCase.Fork) switch
    {
        ForkDriver<BeaconStateFulu> fulu => RunOperation(testCase, fulu, FuluOperations[testCase.OperationName], batched),
        ForkDriver<BeaconStateGloas> gloas => RunOperation(testCase, gloas, GloasOperations[testCase.OperationName], batched),
        ForkDriver other => throw new InvalidOperationException($"fork '{other.Fork}' has no state type this differential knows"),
    };

    private static Outcome RunOperation<TState>(OperationCase testCase, ForkDriver<TState> driver, (string File, Action<OpContext<TState>, byte[], BlockSignatureBatch?> Apply) handler, bool batched) where TState : class
    {
        TState state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
        OpContext<TState> ctx = new(
            state,
            driver.NewCache(),
            FuluDriverSupport.BuildPubkeyCache(driver.ValidatorsOf(state)),
            FuluDriverSupport.ShouldVerifySignatures(testCase.CasePath),
            ExecutionValid: true,
            FuluDriverSupport.CaseSpec(testCase.CasePath),
            testCase.CasePath);
        byte[] operand = SszConsensusTestLoader.ReadSszSnappy(Path.Combine(testCase.CasePath, handler.File));

        try
        {
            if (batched)
                BlockSignatureBatch.Run(batch => handler.Apply(ctx, operand, batch));
            else
                handler.Apply(ctx, operand, null);
        }
        catch (Exception e)
        {
            return new Outcome(e.GetType().FullName, e.Message, null);
        }

        return new Outcome(null, null, driver.StateRoot(state));
    }

    private static IEnumerable<TestCaseData> SanityBlockCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled)
            yield break;

        foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
        {
            string? sanityRoot = ConsensusSpecArchive.SuitePath(ConsensusPreset.Mainnet, fork, "sanity");
            foreach (string caseDir in ConsensusSpecArchive.LeafDirs(sanityRoot is null ? null : Path.Combine(sanityRoot, "blocks"), "meta.yaml"))
            {
                string vectorName = $"{ConsensusPreset.Mainnet}/{fork}/sanity/blocks/{Path.GetFileName(caseDir)}";
                yield return new TestCaseData(new SanityCase(nameof(ConsensusPreset.Mainnet), fork, caseDir, vectorName)).SetName(vectorName);
            }
        }
    }

    private static IEnumerable<TestCaseData> OperationCases()
    {
        if (!ConsensusSpecArchive.MainnetEnabled)
            yield break;

        foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
        {
            string? operationsRoot = ConsensusSpecArchive.SuitePath(ConsensusPreset.Mainnet, fork, "operations");
            IEnumerable<string> table = fork == "gloas" ? GloasOperations.Keys : FuluOperations.Keys;
            foreach (string opName in table)
            {
                foreach (string caseDir in ConsensusSpecArchive.LeafDirs(operationsRoot is null ? null : Path.Combine(operationsRoot, opName), "pre.ssz_snappy"))
                {
                    string vectorName = $"{ConsensusPreset.Mainnet}/{fork}/operations/{opName}/{Path.GetFileName(caseDir)}";
                    yield return new TestCaseData(new OperationCase(nameof(ConsensusPreset.Mainnet), fork, opName, caseDir, vectorName)).SetName(vectorName);
                }
            }
        }
    }

    private sealed class AcceptingNotifier : INewPayloadNotifier
    {
        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
        public ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests) => ExecutionStatus.Valid;
    }
}
