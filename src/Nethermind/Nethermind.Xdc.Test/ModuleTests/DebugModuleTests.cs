// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Test;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests;

/// <summary>
/// Verifies that <see cref="DebugRpcModule"/>'s raw header/block endpoints — which resolve their
/// decoder from the static <see cref="Rlp"/> registry rather than DI — encode XDC headers correctly
/// once <see cref="XdcHeaderModule"/> is loaded. Mirrors <c>Nethermind.JsonRpc.Test.Modules.DebugModuleTests</c>.
/// </summary>
[TestFixture, NonParallelizable]
public class DebugModuleTests
{
    private readonly IJsonRpcConfig _jsonRpcConfig = new JsonRpcConfig();
    private readonly ISpecProvider _specProvider = SpecProviderSubstitute.Create();
    private readonly IDebugBridge _debugBridge = Substitute.For<IDebugBridge>();
    private readonly IBlockFinder _blockFinder = Substitute.For<IBlockFinder>();
    private readonly IBlockchainBridge _blockchainBridge = Substitute.For<IBlockchainBridge>();

    [SetUp]
    public void Setup() =>
        new ContainerBuilder().AddModule(new XdcHeaderModule()).Build();

    [TearDown]
    public void TearDown() => Rlp.ResetDecoders();

    private DebugRpcModule CreateModule() => new(
        LimboLogs.Instance,
        _debugBridge,
        _jsonRpcConfig,
        _specProvider,
        _blockchainBridge,
        new BlocksConfig(),
        _blockFinder,
        new BlockForRpcFactory());

    private Task<JsonRpcResponse> Request(string method, params object?[]? parameters) =>
        RpcTest.TestRequest<IDebugRpcModule>(CreateModule(), method, parameters);

    [Test]
    public async Task DebugGetRawHeader_WhenXdcHeader_ReturnsXdcShapedRlp()
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader().WithNumber(0).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _debugBridge.GetBlock(new BlockParameter(0UL)).Returns(block);

        using JsonRpcResponse response = await Request("debug_getRawHeader", "0x0");

        byte[] actual = RpcTest.AssertSuccess<ArrayPoolList<byte>>(response).AsSpan().ToArray();
        Assert.That(actual, Is.EqualTo(new XdcHeaderDecoder().Encode(header).Bytes));
        Assert.That(actual, Is.Not.EqualTo(new HeaderDecoder().Encode(header).Bytes));
    }

    [Test]
    public async Task DebugGetRawBlock_WhenXdcHeader_ReturnsXdcShapedRlp()
    {
        XdcBlockHeader header = Build.A.XdcBlockHeader().WithNumber(1).TestObject;
        Block block = Build.A.Block.WithHeader(header).TestObject;
        _debugBridge.GetBlock(new BlockParameter(1UL)).Returns(block);

        using JsonRpcResponse response = await Request("debug_getRawBlock", "0x1");

        byte[] actual = RpcTest.AssertSuccess<ArrayPoolList<byte>>(response).AsSpan().ToArray();
        Assert.That(actual, Is.EqualTo(new BlockDecoder(new XdcHeaderDecoder()).Encode(block).Bytes));
        Assert.That(actual, Is.Not.EqualTo(new BlockDecoder(new HeaderDecoder()).Encode(block).Bytes));
    }
}
