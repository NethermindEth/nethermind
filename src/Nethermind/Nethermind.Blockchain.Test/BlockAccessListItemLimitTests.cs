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
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// The EIP-7928 item limit, <c>block_gas_limit / ITEM_COST</c>, applies to the list execution produces, whether
/// the block arrives with its list (engine API, or reattached from the store for sync, restart replay or a reorg)
/// or without one (RLP import).
/// </summary>
[Parallelizable(ParallelScope.All)]
public class BlockAccessListItemLimitTests
{
    [Test]
    public async Task Item_limit_applies_with_and_without_an_attached_list(
        [Values] bool parallelExecution, [Values] bool attachList, [Values] bool overLimit)
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance))
            .Intercept<IBlocksConfig>(blocksConfig => blocksConfig.ParallelExecution = parallelExecution));
        Block produced = await chain.AddBlock();
        ReadOnlyBlockAccessList list = chain.Container.Resolve<IBlockAccessListStore>().Get(produced.Number, produced.Hash!)
            ?? throw new AssertionException("the produced block has no stored block access list");
        Assert.That(list.ItemCount, Is.Positive, "precondition: system calls fill the list of an empty block");

        // System calls fill the list without spending block gas, so a low enough gas limit puts even an empty
        // block's list over the limit. Execution does not depend on the block's own gas limit, so the list still
        // matches the block.
        BlockHeader header = produced.Header.Clone();
        header.GasLimit = (ulong)list.ItemCount * Eip7928Constants.ItemCost - (overLimit ? 1ul : 0ul);
        header.Hash = header.CalculateHash();
        Block suggested = produced.WithReplacedHeader(header);
        suggested.BlockAccessList = attachList ? list : null;

        using IDisposable scope = chain.MainWorldState.BeginScope(chain.BlockTree.FindHeader(produced.ParentHash!, BlockTreeLookupOptions.None));
        Action process = () => chain.BlockProcessor.ProcessOne(
            suggested, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, chain.SpecProvider.GetSpec(header), CancellationToken.None);

        Assert.That(process, overLimit
            ? Throws.InstanceOf<InvalidBlockException>().With.Message.Contains("BlockAccessListGasLimitExceeded")
            : Throws.Nothing);
    }
}
