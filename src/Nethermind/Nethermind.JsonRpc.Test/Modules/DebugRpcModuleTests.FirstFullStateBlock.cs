// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task Debug_getFirstFullStateBlock_returns_zero_for_archive_chain()
    {
        using Context ctx = await Context.Create();

        ResultWrapper<long?> result = ctx.DebugRpcModule.debug_getFirstFullStateBlock();

        result.Result.ResultType.Should().Be(Core.ResultType.Success);
        result.Data.Should().Be(0);
    }

    [Test]
    public async Task Debug_getFirstFullStateBlock_still_zero_after_adding_blocks()
    {
        using Context ctx = await Context.Create();

        await ctx.Blockchain.AddFunds(TestItem.AddressA, 1.Ether);
        await ctx.Blockchain.AddFunds(TestItem.AddressB, 1.Ether);
        long head = ctx.Blockchain.BlockTree.Head!.Number;
        head.Should().BeGreaterThan(0);

        ResultWrapper<long?> result = ctx.DebugRpcModule.debug_getFirstFullStateBlock();

        result.Result.ResultType.Should().Be(Core.ResultType.Success);
        result.Data.Should().Be(0);
    }
}
