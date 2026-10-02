// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
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

namespace Nethermind.Consensus.Test.Eip8288;

[NonParallelizable]
public class ProofInclusionListEnforcementTests
{
    [TestCase("valid", false)]
    [TestCase("included", true)]
    [TestCase("nonce", true)]
    [TestCase("revert", true)]
    [TestCase("forbidden-touch", true)]
    [TestCase("unfunded", true)]
    [TestCase("withdrawal-funded", true)]
    [TestCase("proof-gas-full", true)]
    [TestCase("missing-proof", false)]
    [TestCase("bad-proof", false)]
    [TestCase("wrong-commitment", false)]
    public async Task Enforces_proven_frame_prefixes_through_production_processor(string scenario, bool satisfied)
    {
        OverridableReleaseSpec spec = new(Eip8288Prototype.Instance) { IsEip7805Enabled = true };
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(spec))
            .AddSingleton<ILeanProofVerifier>(new FakeLeanProofVerifier(scenario != "bad-proof")));
        IWorldState state = chain.MainWorldState;
        using System.IDisposable stateScope = state.BeginScope(chain.BlockTree.Head!.Header);
        Address sender = TestItem.PrivateKeyA.Address;
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
        state.Commit(spec);

        byte[] dependency = new byte[Eip8288Constants.DependencyTripleLength];
        dependency[31] = Eip8288Constants.LeanSphincsScheme;
        TxFrame[] frames = [FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, dependency)];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = chain.SpecProvider.ChainId,
            Nonce = scenario == "nonce" ? 1UL : 0UL,
            SenderAddress = sender,
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyA, sender);
        transaction.Hash = transaction.CalculateHash();
        Block block = Build.A.Block.WithNumber(1).WithGasLimit(30_000_000)
            .WithBaseFeePerGas(0).WithBeneficiary(TestItem.AddressD)
            .WithTransactions(scenario == "included" ? [transaction] : []).TestObject;
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
        block.InclusionListTransactions = [transaction];
        if (scenario == "withdrawal-funded")
            block = block.WithReplacedBody(new(block.Transactions, block.Uncles,
                [new Withdrawal { Address = sender, AmountInGwei = 1_000_000_000 }]));
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.ForTransaction(transaction));
        block.InclusionListRecursiveStark = scenario == "missing-proof" ? null
            : new RecursiveStark([1], scenario == "wrong-commitment" ? Keccak.Zero : new Hash256(depsHash));
        (ulong, ulong)? originalDimensions = block.Header.GasUsedPerDimension;
        ulong originalGasUsed = block.GasUsed;
        UInt256 balance = state.GetBalance(sender);
        ValueHash256 codeHash = state.GetCodeHash(sender);
        IInclusionListSatisfactionChecker checker = ((MainProcessingContext)chain.MainProcessingContext)
            .LifetimeScope.Resolve<IInclusionListSatisfactionChecker>();

        Assert.That(checker.IsSatisfied(block, block, state), Is.EqualTo(satisfied));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetBalance(sender), Is.EqualTo(balance));
            Assert.That(state.GetCodeHash(sender), Is.EqualTo(codeHash));
            Assert.That(state.GetNonce(sender), Is.Zero);
            Assert.That(block.Header.GasUsedPerDimension, Is.EqualTo(originalDimensions));
            Assert.That(block.GasUsed, Is.EqualTo(originalGasUsed));
        }
    }
}
