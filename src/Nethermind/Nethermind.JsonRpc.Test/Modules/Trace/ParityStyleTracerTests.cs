// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Specs;
using NUnit.Framework;
using NSubstitute;
using Nethermind.Core.Test.Modules;
using Nethermind.JsonRpc.Modules;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.JsonRpc.Test.Modules.Trace;

[Parallelizable(ParallelScope.Self)]
public class ParityStyleTracerTests
{
    private BlockTree? _blockTree;
    private IPoSSwitcher? _poSSwitcher;
    private ITraceRpcModule _traceRpcModule;
    private IContainer _container;

    [SetUp]
    public async Task Setup()
    {
        ISpecProvider specProvider = MainnetSpecProvider.Instance;

        _blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithSpecProvider(specProvider)
            .TestObject;

        ChainSpec cp = Build.A.ChainSpec
            .WithAllocation(new Address("0xdea60e4f8ea50d5ed92b0a5b15ae9d24aeba0bee"), 1.Ether() )
            .TestObject;

        _poSSwitcher = Substitute.For<IPoSSwitcher>();
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(cp))
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton<IPoSSwitcher>(_poSSwitcher)
            .AddSingleton<IBlockTree>(_blockTree)
            .Build();

        await _container.Resolve<PseudoNethermindRunner>().StartBlockProcessing(default);
        _traceRpcModule = _container.Resolve<IRpcModuleFactory<ITraceRpcModule>>().Create();
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _container.DisposeAsync();
    }

    [Test]
    public void Can_trace_raw_parity_style()
    {
        ResultWrapper<ParityTxTraceFromReplay> result = _traceRpcModule.trace_rawTransaction(Bytes.FromHexString("f889808609184e72a00082271094000000000000000000000000000000000000000080a47f74657374320000000000000000000000000000000000000000000000000000006000571ca08a8bbf888cfa37bbf0bb965423625641fc956967b81d12e23709cead01446075a01ce999b56a8a88504be365442ea61239198e23d1fce7d00fcfc5cd3b44b7215f"), new[] { "trace" });
        Assert.That(result.Data, Is.Not.Null);
    }

    [Test]
    public void Can_trace_raw_parity_style_berlin_tx()
    {
        ResultWrapper<ParityTxTraceFromReplay> result = _traceRpcModule.trace_rawTransaction(Bytes.FromHexString("01f85b821e8e8204d7847735940083030d408080853a60005500c080a0f43e70c79190701347517e283ef63753f6143a5225cbb500b14d98eadfb7616ba070893923d8a1fc97499f426524f9e82f8e0322dfac7c3d7e8a9eee515f0bcdc4"), new[] { "trace" });
        Assert.That(result.Data, Is.Not.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Should_return_correct_block_reward(bool isPostMerge)
    {
        Block block = Build.A.Block.WithParent(_blockTree!.Head!).TestObject;
        _blockTree!.SuggestBlock(block).Should().Be(AddBlockResult.Added);
        _poSSwitcher!.IsPostMerge(Arg.Any<BlockHeader>()).Returns(isPostMerge);

        ParityTxTraceFromStore[] result = _traceRpcModule.trace_block(new BlockParameter(block.Number)).Data.ToArray();
        if (isPostMerge)
        {
            result.Length.Should().Be(1);
            result[0].Action.Author.Should().Be(block.Beneficiary!);
            result[0].Action.Value.Should().Be(0);
        }
        else
        {
            result.Length.Should().Be(0);
        }
    }
}
