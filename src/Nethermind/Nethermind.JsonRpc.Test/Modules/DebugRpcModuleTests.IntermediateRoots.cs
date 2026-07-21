// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task Debug_intermediateRoots_returns_one_root_per_transaction()
    {
        using Context context = await Context.Create();
        await context.Blockchain.AddBlock(CreateTraceBlockTransactions(context.Blockchain));

        Hash256 blockHash = context.Blockchain.BlockTree.Head!.Hash!;
        ResultWrapper<IReadOnlyCollection<Hash256>> result = context.DebugRpcModule.debug_intermediateRoots(blockHash);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        Assert.That(result.Data, Has.Count.EqualTo(2), "the block has exactly two user transactions");
        Assert.That(result.Data, Is.Unique, "each tx mutates state, producing a distinct post-tx root");
        Assert.That(result.Data, Does.Not.Contain(Keccak.Zero));
    }

    [Test]
    public async Task Debug_intermediateRoots_rejects_genesis()
    {
        using Context context = await Context.Create();
        Hash256 genesisHash = context.Blockchain.BlockTree.Genesis!.Hash!;

        ResultWrapper<IReadOnlyCollection<Hash256>> result = context.DebugRpcModule.debug_intermediateRoots(genesisHash);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidInput));
        Assert.That(result.Result.Error, Does.Contain("genesis"));
    }
}
