// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Decoders;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

[NonParallelizable]
public class NativeInclusionListTests
{
    [TestCase("uncovered", false)]
    [TestCase("malformed", false)]
    [TestCase("bad-proof", true)]
    public async Task Real_proof_membership_preserves_independent_inclusion_list_obligations(string scenario, bool frameSuppressed)
    {
        OverridableReleaseSpec spec = new(Eip8288Prototype.Instance) { IsEip7805Enabled = true };
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(spec)));
        Transaction covered = NativeBlockProductionTests.CreateTransaction(chain);
        Transaction ordinary = Build.A.Transaction.WithNonce(0).WithGasLimit(21_000).WithGasPrice(1.GWei)
            .SignedAndResolved(TestItem.PrivateKeyC).TestObject;
        Transaction bad = NativeBlockProductionTests.CreateTransaction(chain);
        FrameDependency extra = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("uncovered"), default);
        bad.Frames = scenario == "malformed"
            ? [new(FrameMode.DepVerify, FrameFlags.None, null, 0, 0, new byte[1])]
            : [FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
                new(FrameMode.DepVerify, FrameFlags.None, null, extra.VerificationGas, 0, Eip8288Dependencies.Serialize([extra]))];
        bad.GasLimit = FrameTxValidation.TotalGasLimit(bad.Frames);
        FrameTxTestFrames.SignSecp256k1(bad, TestItem.PrivateKeyB, bad.SenderAddress!);
        bad.Hash = bad.CalculateHash();
        if (scenario == "uncovered") Assert.That(FrameTxValidation.IsWellFormed(bad, spec, out string? error), Is.True, error);
        FrameDependency[] dependencies = [.. Eip8288Dependencies.ForTransaction(covered)];
        ValueHash256 commitment = Eip8288Dependencies.ComputeDepsHash(dependencies);
        byte[] proofBytes = NativeLeanProofVerifier.Instance.ProveRecursiveStark(commitment, Eip8288Constants.AggregatedVk,
            new AggregationInput { Deps = dependencies, Witnesses = [NativeLeanProofVerifierTests.Witness("sphincs"), NativeLeanProofVerifierTests.Witness("stark")] });
        if (scenario == "bad-proof") proofBytes[^1] ^= 1;
        RecursiveStark proof = new(proofBytes, new Hash256(commitment));
        byte[] metadata = Eip8288Dependencies.Serialize(dependencies);
        byte[][] encoded = InclusionListDecoder.Encode([ordinary, bad, covered]);
        PayloadAttributes attributes = new()
        {
            InclusionListTransactions = encoded,
            InclusionListRecursiveStark = proof,
            InclusionListProvenDependencies = metadata
        };
        InclusionListTxSource source = new(new EthereumEcdsa(chain.SpecProvider.ChainId), chain.SpecProvider,
            LimboLogs.Instance, NativeLeanProofVerifier.Instance);
        source.Set(encoded, spec, proof, metadata);
        BlockToProduce block = new(Build.A.BlockHeader.WithNumber(1).WithGasLimit(30_000_000).TestObject);
        source.ApplyInclusionList(block, attributes);
        Transaction[] offered = [.. source.GetTransactions(chain.BlockTree.Head!.Header, block.Header, block.GasLimit, attributes)];
        Assert.That(offered.Select(tx => tx.Hash), frameSuppressed
            ? Is.EqualTo(new[] { ordinary.Hash }) : Is.EquivalentTo(new[] { ordinary.Hash, covered.Hash }));
        Assert.That(block.InclusionListTransactions, Has.Length.EqualTo(3));
        using (chain.MainWorldState.BeginScope(chain.BlockTree.Head!.Header))
        {
            block.Header.GasUsedPerDimension = (0, 0);
            IInclusionListSatisfactionChecker checker = ((MainProcessingContext)chain.MainProcessingContext)
                .LifetimeScope.Resolve<IInclusionListSatisfactionChecker>();
            Block ordinaryOnly = block.WithReplacedHeader(block.Header.Clone());
            ordinaryOnly.InclusionListTransactions = [ordinary];
            ordinaryOnly.InclusionListRecursiveStark = null;
            ordinaryOnly.InclusionListProvenDependencies = null;
            Assert.That(checker.IsSatisfied(ordinaryOnly, ordinaryOnly, chain.MainWorldState), Is.False,
                "the funded code-free ordinary sender is independently appendable");
            Assert.That(checker.IsSatisfied(block, block, chain.MainWorldState), Is.False,
                "a bad frame or package cannot excuse omitting the valid ordinary entry");
        }
    }
}
