// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>Block-level tests of EIP-7709 <c>BLOCKHASH</c>, through block production, validation and stateless replay.</summary>
[Parallelizable(ParallelScope.All)]
public class Eip7709BlockTests
{
    private static readonly Address Reader = TestItem.AddressF;

    // Stores BLOCKHASH(NUMBER - 1) in slot 0 and BLOCKHASH(NUMBER - 2) in slot 1.
    private static readonly byte[] StoreParentAndGrandparentHashes = Bytes.FromHexString(
        "0x43600190034060005543600290034060015500");

    [Test]
    public async Task Blockhash_is_served_from_history_storage_and_recorded_in_the_block_access_list([Values] bool parallelExecution)
    {
        using BasicTestBlockchain chain = await CreateChain(parallelExecution);
        await chain.AddBlock();
        Block block = await chain.AddBlock(CallReader(0));
        BlockHeader parent = chain.BlockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None)!;
        ReadOnlyAccountChanges? history = GetBlockAccessList(chain, block).GetAccountChanges(Eip2935Constants.BlockHashHistoryAddress);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.ReceiptStorage.Get(block)[0].StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(GetStorage(chain, block, UInt256.Zero), Is.EqualTo(parent.Hash!.ToUInt256()));
            Assert.That(GetStorage(chain, block, UInt256.One), Is.EqualTo(parent.ParentHash!.ToUInt256()));
            // The parent's slot is written by this block's EIP-2935 system call, so its read folds into that change.
            Assert.That(history?.StorageReads, Is.EqualTo(new[] { new UInt256(block.Number - 2) }));
        }
    }

    /// <summary>The history slots the opcode reads are in the witness, so a stateless replay needs no ancestor headers for them.</summary>
    [Test]
    public async Task Blockhash_witness_replays_statelessly_to_the_same_state_root()
    {
        using BasicTestBlockchain chain = await CreateChain(parallelExecution: false);
        await chain.AddBlock();
        Block block = await chain.AddBlock(CallReader(0));
        BlockHeader parent = chain.BlockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None)!;

        using IWitnessGeneratingBlockProcessingEnvScope witnessScope = chain.Container.Resolve<IWitnessGeneratingBlockProcessingEnvFactory>().CreateScope();
        using Witness witness = witnessScope.Env.CreateExistingBlockWitnessCollector().GetWitnessForExistingBlock(parent, block);
        StatelessBlockProcessingEnv stateless = new(witness, chain.SpecProvider, Always.Valid, LimboLogs.Instance);
        using IDisposable stateScope = stateless.WorldState.BeginScope(parent);
        (Block processed, _) = stateless.BlockProcessor.ProcessOne(
            block.WithReplacedHeader(block.Header.Clone()), ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, chain.SpecProvider.GetSpec(block.Header), CancellationToken.None);

        stateless.WorldState.Get(new StorageCell(Reader, UInt256.One), out UInt256 grandparentHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processed.Header.StateRoot, Is.EqualTo(block.Header.StateRoot));
            Assert.That(grandparentHash, Is.EqualTo(parent.ParentHash!.ToUInt256()));
        }
    }

    private static async Task<BasicTestBlockchain> CreateChain(bool parallelExecution)
    {
        IReleaseSpec spec = new OverridableReleaseSpec(Bogota.Instance) { IsEip7709Enabled = true };
        return await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(spec))
            .Intercept<IBlocksConfig>(blocksConfig => blocksConfig.ParallelExecution = parallelExecution)
            .WithGenesisPostProcessor((_, state, specProvider) =>
            {
                state.CreateAccount(Reader, UInt256.Zero);
                state.InsertCode(Reader, StoreParentAndGrandparentHashes, specProvider.GenesisSpec);
            }));
    }

    private static Transaction CallReader(ulong nonce) =>
        Build.A.Transaction.WithNonce(nonce).WithTo(Reader).WithGasLimit(1_000_000)
            .SignedAndResolved(TestItem.PrivateKeyB).TestObject;

    private static ReadOnlyBlockAccessList GetBlockAccessList(BasicTestBlockchain chain, Block block) =>
        chain.Container.Resolve<IBlockAccessListStore>().Get(block.Number, block.Hash!)
        ?? throw new AssertionException($"block {block.Number} has no stored block access list");

    private static UInt256 GetStorage(BasicTestBlockchain chain, Block block, in UInt256 index)
    {
        chain.StateReader.GetStorage(block.Header, Reader, index, out UInt256 value);
        return value;
    }
}
