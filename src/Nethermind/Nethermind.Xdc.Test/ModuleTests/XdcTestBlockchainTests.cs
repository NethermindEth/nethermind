// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;
using Nethermind.Xdc.RPC;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;
using System;
using System.Threading.Tasks;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider;

namespace Nethermind.Xdc.Test.ModuleTests;

internal class XdcTestBlockchainTests
{
    private XdcTestBlockchain _blockchain;

    [SetUp]
    public async Task Setup() =>
        _blockchain = await XdcTestBlockchain.Create();

    [TearDown]
    public void TearDown() =>
        _blockchain?.Dispose();

    [Test]
    public void RpcModulesResolveFromTheContainer([Values(typeof(IXdcRpcModule), typeof(IXdcExtendedEthRpcModule), typeof(IXdcMasternodeEthRpcModule))] Type moduleType) =>
        Assert.That(_blockchain.Container.Resolve(moduleType), Is.Not.Null);

    /// <remarks>
    /// A JSON-RPC method name is a single global key and the provider keeps whichever module registered
    /// last, silently. The XDC override of <c>eth_getAccountInfo</c> only wins because plugins register
    /// after the core modules, so this fails the moment that ordering changes rather than the node
    /// quietly serving the core client's account shape on an XDC chain.
    /// </remarks>
    [Test]
    public void XdcOverridesTheCoreAccountInfoEndpoint()
    {
        IRpcModuleProvider provider = _blockchain.Container.Resolve<IRpcModuleProvider>();

        ResolvedMethodInfo? resolved = provider.Resolve("eth_getAccountInfo");

        Assert.That(resolved, Is.Not.Null);
        Assert.That(resolved!.MethodInfo.DeclaringType, Is.EqualTo(typeof(IXdcExtendedEthRpcModule)));
    }

    [Test]
    public async Task SetupXdcChainAndValidateAllHeaders([Values(180, 91)] int count)
    {
        //Shorten the epoch length so we can run the test faster
        _blockchain.ChangeReleaseSpec((c) =>
        {
            c.EpochLength = 90UL;
            c.Gap = 45UL;
        });

        await _blockchain.AddBlocks(count);
        IHeaderValidator headerValidator = _blockchain.Container.Resolve<IHeaderValidator>();
        BlockHeader parent = _blockchain.BlockTree.Genesis!;
        for (ulong i = 1; i < _blockchain.BlockTree.Head!.Number; i++)
        {
            Block? block = _blockchain.BlockTree.FindBlock(i, BlockTreeLookupOptions.None);
            Assert.That(block, Is.Not.Null);
            Assert.That(headerValidator.Validate(block!.Header, parent, false, out string? error), Is.True, "Header validation failed: " + error);
            parent = block.Header;
        }
    }
}
