// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// EIP-7928 block access indices never exceed <c>len(transactions) + 1</c>. A list reattached from the store for
/// queued processing (sync, restart replay, reorg) skipped the engine API's pre-execution bounds check, and execution
/// never reads a change past the post-execution index, so the block must still be rejected rather than have the
/// change applied to state.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class BlockAccessListIndexBoundsTests
{
    [Test]
    public async Task Attached_list_with_change_past_post_execution_index_is_rejected([Values] bool parallelExecution)
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance))
            .Intercept<IBlocksConfig>(blocksConfig => blocksConfig.ParallelExecution = parallelExecution));
        Block produced = await chain.AddBlock();
        ReadOnlyBlockAccessList list = chain.Container.Resolve<IBlockAccessListStore>().Get(produced.Number, produced.Hash!)
            ?? throw new AssertionException("the produced block has no stored block access list");

        // Repeating the slot's final value past the post-execution index leaves the honest state root valid, so only
        // the index bound can reject the block.
        ReadOnlyAccountChanges[] accounts = list.AccountChanges.AsSpan().ToArray();
        int accountIndex = Array.FindIndex(accounts, static a => a.StorageChanges.Length > 0);
        Assert.That(accountIndex, Is.Not.Negative, "precondition: system calls write storage in an empty block");
        ReadOnlyAccountChanges account = accounts[accountIndex];
        ReadOnlySlotChanges slot = account.StorageChanges[0];
        StorageChange spurious = new((uint)produced.Transactions.Length + 2, slot.Changes[^1].Value);
        accounts[accountIndex] = new ReadOnlyAccountChanges(account.Address,
            [new ReadOnlySlotChanges(slot.Key, [.. slot.Changes, spurious]), .. account.StorageChanges[1..]],
            account.StorageReads, account.BalanceChanges, account.NonceChanges, account.CodeChanges);
        ReadOnlyBlockAccessList tampered = new(accounts, list.ItemCount);

        BlockHeader header = produced.Header.Clone();
        header.BlockAccessListHash = Keccak.Compute(Rlp.Encode(tampered).Bytes);
        header.Hash = header.CalculateHash();
        Block suggested = produced.WithReplacedHeader(header);
        suggested.BlockAccessList = tampered;

        using IDisposable scope = chain.MainWorldState.BeginScope(chain.BlockTree.FindHeader(produced.ParentHash!, BlockTreeLookupOptions.None));
        Action process = () => chain.BlockProcessor.ProcessOne(
            suggested, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, chain.SpecProvider.GetSpec(header), CancellationToken.None);

        Assert.That(process, Throws.InstanceOf<InvalidBlockException>().With.Message.Contains("exceeds allowed range"));
    }
}
