// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class ExistingBlockWitnessTests
{
    // Increments the slot named by the first calldata word, then zeroes the slot named by the second.
    // PUSH1 0 CALLDATALOAD DUP1 SLOAD PUSH1 1 ADD SWAP1 SSTORE PUSH1 0 PUSH1 32 CALLDATALOAD SSTORE STOP
    private static readonly byte[] IncrementAndClear = Bytes.FromHexString("6000358054600101905560006020355500");

    private const int Contracts = 24;
    private const int SlotsPerContract = 64;

    [Test]
    public async Task Witness_of_a_block_touching_many_storage_tries_replays_statelessly()
    {
        Address[] contracts = [.. Enumerable.Range(0, Contracts).Select(static i => new Address(Keccak.Compute($"contract{i}").Bytes[..Address.Size]))];
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance))
            .WithGenesisPostProcessor((_, state, specProvider) =>
            {
                foreach (Address contract in contracts)
                {
                    state.CreateAccount(contract, UInt256.One);
                    state.InsertCode(contract, IncrementAndClear, specProvider.GenesisSpec);
                    for (int slot = 1; slot <= SlotsPerContract; slot++)
                        state.Set(new StorageCell(contract, (UInt256)slot), (UInt256)slot);
                }
            }));

        // Every contract gets an update and a deletion, so each storage trie needs both its paths and any collapse sibling.
        Transaction[] txs = [.. contracts.Select((contract, i) => Build.A.Transaction
            .WithNonce((ulong)i)
            .WithTo(contract)
            .WithData([.. ((UInt256)(i % SlotsPerContract + 1)).ToBigEndian(), .. ((UInt256)((i + 7) % SlotsPerContract + 1)).ToBigEndian()])
            .WithGasLimit(100_000)
            .WithGasPrice(1.GWei)
            .SignedAndResolved(TestItem.PrivateKeyB).TestObject)];
        Block block = await chain.AddBlock(txs);
        Assert.That(block.Transactions, Has.Length.EqualTo(Contracts), "precondition: every call landed in one block");

        BlockHeader parent = chain.BlockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None)!;
        using IWitnessGeneratingBlockProcessingEnvScope scope = chain.Container.Resolve<IWitnessGeneratingBlockProcessingEnvFactory>().CreateScope();
        using Witness witness = scope.Env.CreateExistingBlockWitnessCollector().GetWitnessForExistingBlock(parent, block);

        StatelessBlockProcessingEnv stateless = new(witness, chain.SpecProvider, Always.Valid, LimboLogs.Instance);
        using IDisposable stateScope = stateless.WorldState.BeginScope(parent);
        (Block processed, _) = stateless.BlockProcessor.ProcessOne(
            block.WithReplacedHeader(block.Header.Clone()), ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, chain.SpecProvider.GetSpec(block.Header), CancellationToken.None);

        Assert.That(processed.Header.StateRoot, Is.EqualTo(block.Header.StateRoot));
    }

    /// <summary>A replay that reproduces the state but not another execution outcome still fails, naming that outcome.</summary>
    [Test]
    public async Task Witness_of_a_block_whose_replay_differs_outside_the_state_fails_naming_the_field()
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance)));
        Block block = await chain.AddBlock();
        BlockHeader header = block.Header.Clone();
        header.ReceiptsRoot = Keccak.Zero;
        header.Hash = header.CalculateHash();
        Block differing = new(header, block.Body);

        BlockHeader parent = chain.BlockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None)!;
        using IWitnessGeneratingBlockProcessingEnvScope scope = chain.Container.Resolve<IWitnessGeneratingBlockProcessingEnvFactory>().CreateScope();
        InvalidBlockException failure = Assert.Throws<InvalidBlockException>(() =>
            scope.Env.CreateExistingBlockWitnessCollector().GetWitnessForExistingBlock(parent, differing));

        Assert.That(failure.Message, Does.StartWith("InvalidReceiptsRoot"));
    }

    // PREVRANDAO, MSTORE at 0, LOG0 the 32 bytes, RETURN nothing: the receipts root commits to the value the opcode reads.
    private static readonly byte[] PrevRandaoLoggingInitCode = [0x44, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xa0, 0x60, 0x00, 0x60, 0x00, 0xf3];

    /// <summary>
    /// Storage does not keep the post-merge flag, so a block read back from it must still replay PREVRANDAO from the
    /// mix hash rather than the difficulty.
    /// </summary>
    [Test]
    public async Task Witness_of_a_post_merge_block_read_back_without_its_flag_replays_PREVRANDAO_from_the_mix_hash()
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Paris.Instance))
            .AddSingleton<IPoSSwitcher>(AlwaysPoS.Instance));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block suggested = Build.A.Block
            .WithParent(parent)
            .WithDifficulty(0)
            .WithMixHash(TestItem.KeccakB)
            .WithPostMergeFlag(true)
            .WithTransactions(Build.A.Transaction.WithNonce(0).WithCode(PrevRandaoLoggingInitCode).WithGasLimit(1_000_000)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject)
            .TestObject;
        // Ground truth mirroring live processing, where the flag is set.
        Block block = chain.BranchProcessor.Process(parent, [suggested], ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation,
            NullBlockTracer.Instance)[0];
        BlockHeader storedHeader = block.Header.Clone();
        storedHeader.IsPostMerge = false;
        storedHeader.TotalDifficulty = null;
        Block stored = new(storedHeader, block.Body);

        using IWitnessGeneratingBlockProcessingEnvScope scope = chain.Container.Resolve<IWitnessGeneratingBlockProcessingEnvFactory>().CreateScope();
        using Witness witness = scope.Env.CreateExistingBlockWitnessCollector().GetWitnessForExistingBlock(parent, stored);

        StatelessBlockProcessingEnv stateless = new(witness, chain.SpecProvider, Always.Valid, LimboLogs.Instance);
        using IDisposable stateScope = stateless.WorldState.BeginScope(parent);
        (Block processed, _) = stateless.BlockProcessor.ProcessOne(block.WithReplacedHeader(block.Header.Clone()),
            ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, chain.SpecProvider.GetSpec(block.Header), CancellationToken.None);

        Assert.That(processed.Hash, Is.EqualTo(block.Hash));
    }
}
