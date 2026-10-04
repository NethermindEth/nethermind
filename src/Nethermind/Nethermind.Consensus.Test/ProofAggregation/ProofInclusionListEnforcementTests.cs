// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using System.Collections.Generic;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Test;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

[NonParallelizable]
public class ProofInclusionListEnforcementTests
{
    [TestCase("valid", false)]
    [TestCase("bounded-proof", false)]
    [TestCase("generic-full", true)]
    [TestCase("generic-space", false)]
    [TestCase("generic-compressed-union", false)]
    [TestCase("generic-covered-and-new", false)]
    [TestCase("included", true)]
    [TestCase("nonce", true)]
    [TestCase("revert", true)]
    [TestCase("forbidden-touch", true)]
    [TestCase("unfunded", true)]
    [TestCase("withdrawal-funded", true)]
    [TestCase("proof-gas-full", true)]
    [TestCase("missing-proof", true)]
    [TestCase("bad-proof", true)]
    [TestCase("full-bad-proof", true)]
    [TestCase("wrong-commitment", true)]
    [TestCase("mixed-omit-frame", false)]
    [TestCase("mixed-omit-legacy", false)]
    [TestCase("mixed-included", true)]
    [TestCase("mixed-malformed-omit-frame", false)]
    [TestCase("mixed-malformed-omit-legacy", false)]
    [TestCase("mixed-uncovered-omit-frame", false)]
    [TestCase("mixed-invalid-proof-omit-legacy", false)]
    [TestCase("mixed-invalid-proof-frame-only", true)]
    public async Task Enforces_proven_frame_prefixes_through_production_processor(string scenario, bool satisfied)
    {
        OverridableReleaseSpec spec = new(Eip8288Prototype.Instance) { IsEip7805Enabled = true };
        FakeLeanProofVerifier verifier = new(scenario is not ("bad-proof" or "full-bad-proof")
            && !scenario.StartsWith("mixed-invalid-proof", System.StringComparison.Ordinal));
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(spec))
            .AddSingleton<ILeanProofVerifier>(verifier));
        IWorldState state = chain.MainWorldState;
        using System.IDisposable stateScope = state.BeginScope(chain.BlockTree.Head!.Header);
        Address sender = TestItem.PrivateKeyA.Address;
        bool mixed = scenario.StartsWith("mixed-", System.StringComparison.Ordinal);
        byte[] approve = Prepare.EvmCode.PushData((byte)FrameFlags.ApproveExecutionAndPayment)
            .PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        byte[] code = scenario switch
        {
            "revert" => Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done,
            "forbidden-touch" => Prepare.EvmCode.PushData(TestItem.AddressD).Op(Instruction.BALANCE).Op(Instruction.POP).Done,
            _ => approve
        };
        state.InsertCode(sender, code, spec);
        state.SubtractFromBalance(sender, state.GetBalance(sender), spec, out _);
        if (scenario != "unfunded") state.AddToBalance(sender, 1.Ether, spec, out _);
        if (mixed) state.AddToBalance(TestItem.PrivateKeyB.Address, 1.Ether, spec, out _);
        state.Commit(spec);

        byte[] dependency = new byte[Eip8288Constants.DependencyTripleLength];
        bool generic = scenario == "bounded-proof" || scenario.StartsWith("generic-", System.StringComparison.Ordinal);
        dependency[31] = generic ? Eip8288Constants.LeanStarkScheme : Eip8288Constants.LeanSphincsScheme;
        if (generic) dependency[32] = 255;
        bool coveredAndNew = scenario == "generic-covered-and-new";
        if (coveredAndNew)
        {
            dependency = new byte[2 * Eip8288Constants.DependencyTripleLength];
            dependency[31] = Eip8288Constants.LeanSphincsScheme;
            dependency[Eip8288Constants.DependencyTripleLength + 31] = Eip8288Constants.LeanStarkScheme;
            dependency[Eip8288Constants.DependencyTripleLength + 32] = 255;
        }
        TxFrame[] frames = [FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null, coveredAndNew ? Eip8288Constants.LeanStarkVerificationGas + Eip8288Constants.LeanSphincsVerificationGas
                : generic ? Eip8288Constants.LeanStarkVerificationGas : Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, dependency)];
        if (mixed) frames = [.. frames, new(FrameMode.PostTx, FrameFlags.None, TestItem.AddressD, 10_000, UInt256.Zero, default)];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = chain.SpecProvider.ChainId,
            Nonce = scenario == "nonce" ? 1UL : 0UL,
            NonceKeys = [0],
            SenderAddress = sender,
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyA, sender);
        transaction.Hash = transaction.CalculateHash();
        Transaction legacy = Build.A.Transaction.WithNonce(0).WithGasLimit(21_000).WithGasPrice(1.GWei)
            .SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Transaction[] includedTransactions = scenario switch
        {
            "included" or "mixed-omit-legacy" or "mixed-malformed-omit-legacy" or "mixed-invalid-proof-omit-legacy" => [transaction],
            "mixed-omit-frame" or "mixed-malformed-omit-frame" or "mixed-uncovered-omit-frame" or "mixed-invalid-proof-frame-only" => [legacy],
            "mixed-included" => [legacy, transaction],
            _ => []
        };
        Block block = Build.A.Block.WithNumber(1).WithGasLimit(30_000_000)
            .WithBaseFeePerGas(0).WithBeneficiary(TestItem.AddressD)
            .WithTransactions(includedTransactions).TestObject;
        if (scenario.StartsWith("generic-", System.StringComparison.Ordinal))
        {
            int count = scenario == "generic-full" ? 16 : scenario == "generic-space" ? 15 : coveredAndNew ? 2 : 1;
            byte[] existing = new byte[count * Eip8288Constants.DependencyTripleLength];
            for (int i = 0; i < count; i++)
            {
                existing[i * Eip8288Constants.DependencyTripleLength + 31] = Eip8288Constants.LeanStarkScheme;
                existing[i * Eip8288Constants.DependencyTripleLength + 32] = (byte)i;
            }
            if (coveredAndNew) existing[Eip8288Constants.DependencyTripleLength + 32] = 255;
            Transaction includedGeneric = new()
            {
                Type = TxType.FrameTx,
                Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, (ulong)count * Eip8288Constants.LeanStarkVerificationGas, UInt256.Zero, existing)]
            };
            block = block.WithReplacedBody(new([includedGeneric], block.Uncles, block.Withdrawals));
            List<FrameDependency> existingDependencies = Eip8288Dependencies.ForTransaction(includedGeneric);
            int proofPadding = scenario == "generic-compressed-union" ? Eip8288Constants.MaxMixedGuestProofBytes : 0;
            block.Header.RecursiveStark = new(LeanProofTestEnvelope.Create(existingDependencies, proofPadding),
                new Hash256(Eip8288Dependencies.ComputeDepsHash(existingDependencies)));
        }
        block.Header.GasUsedPerDimension = (0, 0);
        if (scenario == "proof-gas-full")
        {
            Transaction included = new();
            transaction.CopyTo(included, copyHash: false);
            included.Hash = Keccak.Compute("included");
            block = block.WithReplacedBody(new([included], block.Uncles, block.Withdrawals));
            ulong executionUsed = block.GasLimit - transaction.GasLimit - 2 * Eip8288Constants.LeanStarkVerificationGas + 1;
            block.Header.GasUsed = executionUsed + Eip8288Constants.LeanStarkVerificationGas;
            block.Header.GasUsedPerDimension = (executionUsed, 0);
        }
        if (scenario == "full-bad-proof") block.Header.GasUsed = block.GasLimit;
        block.InclusionListTransactions = mixed ? [legacy, transaction] : [transaction];
        if (scenario.StartsWith("mixed-malformed", System.StringComparison.Ordinal)
            || scenario.StartsWith("mixed-uncovered", System.StringComparison.Ordinal))
        {
            byte[] extra = new byte[Eip8288Constants.DependencyTripleLength];
            extra[31] = Eip8288Constants.LeanStarkScheme;
            TxFrame[] extraFrames = [FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
                new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanStarkVerificationGas, UInt256.Zero, extra)];
            Transaction bad = new()
            {
                Type = TxType.FrameTx,
                ChainId = chain.SpecProvider.ChainId,
                NonceKeys = [0],
                SenderAddress = TestItem.PrivateKeyC.Address,
                Frames = extraFrames,
                GasLimit = FrameTxValidation.TotalGasLimit(extraFrames),
                GasPrice = 1.GWei,
                DecodedMaxFeePerGas = 100.GWei
            };
            FrameTxTestFrames.SignSecp256k1(bad, TestItem.PrivateKeyC, bad.SenderAddress);
            if (scenario.StartsWith("mixed-malformed", System.StringComparison.Ordinal))
                bad.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, new byte[1])];
            bad.Hash = bad.CalculateHash();
            block.InclusionListTransactions = [legacy, bad, transaction];
        }
        if (scenario == "withdrawal-funded")
            block = block.WithReplacedBody(new(block.Transactions, block.Uncles,
                [new Withdrawal { Address = sender, AmountInGwei = 1_000_000_000 }]));
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.ForTransaction(transaction));
        List<FrameDependency> inclusionDependencies = Eip8288Dependencies.ForTransaction(transaction);
        int inclusionPadding = scenario is "bounded-proof" or "generic-compressed-union"
            ? Eip8288Constants.MaxMixedGuestProofBytes : 0;
        block.InclusionListRecursiveStark = scenario == "missing-proof" ? null
            : new RecursiveStark(generic ? LeanProofTestEnvelope.Create(inclusionDependencies, inclusionPadding) : [1], scenario == "wrong-commitment" ? Keccak.Zero : new Hash256(depsHash));
        block.InclusionListProvenDependencies = Eip8288Dependencies.Serialize(Eip8288Dependencies.ForTransaction(transaction));
        (ulong, ulong)? originalDimensions = block.Header.GasUsedPerDimension;
        ulong originalGasUsed = block.GasUsed;
        UInt256 balance = state.GetBalance(sender);
        ValueHash256 codeHash = state.GetCodeHash(sender);
        IInclusionListSatisfactionChecker checker = ((MainProcessingContext)chain.MainProcessingContext)
            .LifetimeScope.Resolve<IInclusionListSatisfactionChecker>();

        int verificationCalls = verifier.VerificationCalls;
        Assert.That(checker.IsSatisfied(block, block, state), Is.EqualTo(satisfied));
        using (Assert.EnterMultipleScope())
        {
            if (scenario == "full-bad-proof") Assert.That(verifier.VerificationCalls, Is.EqualTo(verificationCalls));
            Assert.That(state.GetBalance(sender), Is.EqualTo(balance));
            Assert.That(state.GetCodeHash(sender), Is.EqualTo(codeHash));
            Assert.That(state.GetNonce(sender), Is.Zero);
            Assert.That(block.Header.GasUsedPerDimension, Is.EqualTo(originalDimensions));
            Assert.That(block.GasUsed, Is.EqualTo(originalGasUsed));
        }
    }
}
