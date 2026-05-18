// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
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

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Should().HaveCount(2, "the block has exactly two user transactions");
        result.Data.Should().OnlyHaveUniqueItems("each tx mutates state, producing a distinct post-tx root");
        result.Data.Should().NotContain(Keccak.Zero);
    }

    [Test]
    public async Task Debug_intermediateRoots_rejects_genesis()
    {
        using Context context = await Context.Create();
        Hash256 genesisHash = context.Blockchain.BlockTree.Genesis!.Hash!;

        ResultWrapper<IReadOnlyCollection<Hash256>> result = context.DebugRpcModule.debug_intermediateRoots(genesisHash);

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.ErrorCode.Should().Be(ErrorCodes.InvalidInput);
        result.Result.Error.Should().Contain("genesis");
    }
}
