// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class FrameTxParallelBalValidationTests
{
    private static readonly Address Sender = TestItem.AddressF;

    private static readonly byte[] WriteThenRevert = Prepare.EvmCode
        .PushData(1).PushData(1).Op(Instruction.SSTORE)
        .PushData(0).PushData(0).Op(Instruction.REVERT).Done;

    [Test]
    public async Task Frame_transaction_invalidity_outranks_its_own_accesses_missing_from_the_list([Values] bool parallelExecution)
    {
        ConcurrentQueue<(Hash256? Block, ProcessingOptions Options)> attempts = new();
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(FramesDevnet1.Instance))
            .AddDecorator<IBlockProcessor>((_, inner) => new AttemptRecordingBlockProcessor(inner, attempts))
            .Intercept<IBlocksConfig>(blocksConfig => blocksConfig.ParallelExecution = parallelExecution)
            .WithGenesisPostProcessor((_, state, specProvider) =>
            {
                state.CreateAccount(Sender, 1.Ether);
                state.InsertCode(Sender, WriteThenRevert, specProvider.GenesisSpec);
            }));
        Block produced = await chain.AddBlock();
        ReadOnlyBlockAccessList list = chain.Container.Resolve<IBlockAccessListStore>().Get(produced.Number, produced.Hash!)!;

        ReadOnlyAccountChanges[] accounts = list.AccountChanges.AsSpan().ToArray()
            .Append(new ReadOnlyAccountChanges(Sender))
            .OrderBy(static a => a.Address)
            .ToArray();
        ReadOnlyBlockAccessList claimed = new(accounts, list.ItemCount + 1);

        BlockHeader header = produced.Header.Clone();
        header.BlockAccessListHash = Keccak.Compute(Rlp.Encode(claimed));
        header.Hash = header.CalculateHash();
        Block suggested = new(header, [FrameTx()], [], produced.Withdrawals) { BlockAccessList = claimed };
        BlockHeader parent = chain.BlockTree.FindHeader(produced.ParentHash!, BlockTreeLookupOptions.None)!;

        Action process = () => chain.BranchProcessor.Process(parent, [suggested], ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance);

        Assert.That(process, Throws.InstanceOf<InvalidBlockException>().With.Message.Contains("never set a payer"));
        bool[] forcedSequential = [.. attempts.Where(a => a.Block == suggested.Hash).Select(static a => a.Options.ContainsFlag(ProcessingOptions.ForceSequentialBlockAccessList))];
        Assert.That(forcedSequential, Is.EqualTo(parallelExecution ? new[] { false, true } : new[] { false }));
    }

    /// <summary>A custom verifier runs outside any transaction, so the block access list does not declare it, which the
    /// BAL-backed parallel state must not take as a missing account.</summary>
    [Test]
    public async Task Custom_verifier_claim_validates_without_falling_back_to_sequential()
    {
        Address verifier = TestItem.AddressE;
        ConcurrentQueue<(Hash256? Block, ProcessingOptions Options)> attempts = new();
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Eip8141Prototype.Instance))
            .AddDecorator<IBlockProcessor>((_, inner) => new AttemptRecordingBlockProcessor(inner, attempts))
            .Intercept<IBlocksConfig>(blocksConfig => blocksConfig.ParallelExecution = true)
            .WithGenesisPostProcessor((_, state, specProvider) =>
            {
                state.CreateAccount(Sender, 1.Ether);
                state.InsertCode(Sender, Prepare.EvmCode
                    .PushData((byte)FrameFlags.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done, specProvider.GenesisSpec);
                state.CreateAccount(verifier, 0);
                state.InsertCode(verifier, Prepare.EvmCode.StaticCall(IdentityPrecompile.Address, 10_000).Op(Instruction.POP).Op(Instruction.STOP).Done, specProvider.GenesisSpec);
            }));
        Transaction tx = FrameTx(
            new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrameClaim(verifier, default, default, gasLimit: 50_000, witnessLength: 0));

        Block produced = await chain.AddBlock(TestBlockchainUtil.AddBlockFlags.DoNotWaitForHead, tx);
        ReadOnlyBlockAccessList list = chain.Container.Resolve<IBlockAccessListStore>().Get(produced.Number, produced.Hash!)!;
        Block suggested = new(produced.Header, produced.Body, list) { DependencyProof = produced.DependencyProof };
        BlockHeader parent = chain.BlockTree.FindHeader(produced.ParentHash!, BlockTreeLookupOptions.None)!;

        chain.BranchProcessor.Process(parent, [suggested], ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance);

        bool[] forcedSequential = [.. attempts
            .Where(a => a.Block == suggested.Hash && a.Options.ContainsFlag(ProcessingOptions.ReadOnlyChain))
            .Select(static a => a.Options.ContainsFlag(ProcessingOptions.ForceSequentialBlockAccessList))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(produced.Transactions.Select(static t => t.Hash), Is.EqualTo(new[] { tx.Hash }));
            Assert.That(forcedSequential, Is.EqualTo(new[] { false }));
            Assert.That(list.GetAccountChanges(verifier), Is.Null);
        }
    }

    private sealed class AttemptRecordingBlockProcessor(IBlockProcessor inner, ConcurrentQueue<(Hash256? Block, ProcessingOptions Options)> attempts) : IBlockProcessor
    {
        public event Action<Block>? TransactionsExecuted
        {
            add => inner.TransactionsExecuted += value;
            remove => inner.TransactionsExecuted -= value;
        }

        public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
        {
            attempts.Enqueue((suggestedBlock.Hash, options));
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        }
    }

    private static Transaction FrameTx(TxFrame? frame = null, TxFrameClaim? claim = null)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Sender,
            Frames = [frame ?? new TxFrame(FrameMode.Default, FrameFlags.None, Sender, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default)],
            FrameClaims = claim is null ? [] : [claim],
            ClaimWitnesses = claim is null ? null : [[]],
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 1.GWei,
        };
        tx.GasLimit = FrameTxValidation.TotalGasLimit(tx.Frames);
        tx.Hash = tx.CalculateHash();
        return tx;
    }
}
