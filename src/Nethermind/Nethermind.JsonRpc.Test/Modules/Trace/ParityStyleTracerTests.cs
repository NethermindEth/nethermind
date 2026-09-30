// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;
using Nethermind.Core.Test.Modules;
using Nethermind.JsonRpc.Modules;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.JsonRpc.Test.Modules.Trace;

[Parallelizable(ParallelScope.Self)]
public class ParityStyleTracerTests
{
    private BlockTree? _blockTree;
    private ITraceRpcModule _traceRpcModule;
    private IContainer _container;
    private IBlockProcessingQueue _blockProcessingQueue;

    [SetUp]
    public async Task Setup()
    {
        ISpecProvider specProvider = MainnetSpecProvider.Instance;

        _blockTree = Build.A.BlockTree()
            .WithoutSettingHead
            .WithSpecProvider(specProvider)
            .TestObject;

        ChainSpec cp = Build.A.ChainSpec
            .WithAllocation(new Address("0xdea60e4f8ea50d5ed92b0a5b15ae9d24aeba0bee"), 1.Ether)
            .TestObject;

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(cp))
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton<IBlockTree>(_blockTree)
            .Build();

        await _container.Resolve<PseudoNethermindRunner>().StartBlockProcessing(default);
        _traceRpcModule = _container.Resolve<IRpcModuleFactory<ITraceRpcModule>>().Create();
        _blockProcessingQueue = _container.Resolve<IBlockProcessingQueue>();

    }

    [TearDown]
    public async Task TearDownAsync() => await _container.DisposeAsync();

    [Test]
    public void Can_trace_raw_parity_style()
    {
        ResultWrapper<ParityTxTraceFromReplay> result = _traceRpcModule.trace_rawTransaction(Bytes.FromHexString("f8838080829c4094000000000000000000000000000000000000000080a47f74657374320000000000000000000000000000000000000000000000000000006000571ca08a8bbf888cfa37bbf0bb965423625641fc956967b81d12e23709cead01446075a01ce999b56a8a88504be365442ea61239198e23d1fce7d00fcfc5cd3b44b7215f"), new[] { "trace" });
        Assert.That(result.Data, Is.Not.Null);
    }

    [Test]
    public void Can_trace_raw_parity_style_berlin_tx()
    {
        // trace_rawTransaction rejects a transaction for another chain, so it is signed for this one.
        ulong chainId = MainnetSpecProvider.Instance.ChainId;
        Transaction transaction = Build.A.Transaction
            .WithType(TxType.AccessList)
            .WithChainId(chainId)
            .WithTo(null)
            .WithCode(Bytes.FromHexString("3a60005500"))
            .WithGasLimit(200_000)
            .WithGasPrice(0)
            .WithValue(0)
            .SignedAndResolved(new EthereumEcdsa(chainId), TestItem.PrivateKeyA)
            .TestObject;

        ResultWrapper<ParityTxTraceFromReplay> result = _traceRpcModule.trace_rawTransaction(
            TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes, new[] { "trace" });
        Assert.That(result.Data, Is.Not.Null);
    }

    /// <summary>
    /// The chain pays no block reward (as on Taiko or Optimism, which are always post-merge), so the trace has no reward
    /// record.
    /// </summary>
    [Test]
    public async Task Should_not_report_a_reward_the_block_does_not_pay()
    {
        Block block = Build.A.Block.WithParent(_blockTree!.Head!).TestObject;
        Assert.That((await _blockTree!.SuggestBlockAsync(block, BlockTreeSuggestOptions.None)), Is.EqualTo(AddBlockResult.Added));

        ResultWrapper<IEnumerable<ParityTxTraceFromStore>> rpcResult = _traceRpcModule.trace_block(new BlockParameter(block.Number));
        Assert.That(rpcResult.Result, Is.EqualTo(Result.Success));
        Assert.That(rpcResult.Data, Is.Empty);
    }
}
