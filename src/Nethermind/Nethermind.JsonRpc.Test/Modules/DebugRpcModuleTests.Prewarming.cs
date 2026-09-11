// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [TestCase(false, "callTracer")]
    [TestCase(true, "callTracer")]
    [TestCase(false, "")]
    [TestCase(true, "")]
    public async Task TraceBlock_Prewarming_PreservesResultsAndCanonicalState(bool flatDb, string tracer)
    {
        HistoricalBlockView historyView = null!;
        CountingEnvironmentFactory environments = null!;
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsDebug.Returns(true);
        ILogManager logs = Substitute.For<ILogManager>();
        logs.GetClassLogger<HistoricalTracePrewarmer>().Returns(new ILogger(logger));
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithFlatDb(flatDb)
            .Build(builder => builder.AddSingleton<HistoricalTracePrewarmer>(context =>
            {
                historyView = new(context.Resolve<IBlockTree>());
                environments = new(context.Resolve<IReadOnlyTxProcessingEnvFactory>());
                return new(environments, historyView, context.Resolve<IEthereumEcdsa>(), new(2), logs);
            }));

        Block block = await blockchain.AddBlock(CreateTraceBlockTransactions(blockchain));
        Hash256? stateRoot = block.Header.StateRoot;
        UInt256 balance = blockchain.StateReader.GetBalance(block.Header, TestItem.AddressA);
        ulong nonce = blockchain.StateReader.GetNonce(block.Header, TestItem.AddressA);
        GethTraceOptions options = new() { Tracer = tracer };
        IDebugRpcModule module = blockchain.DebugRpcModule;
        string baseline = await RpcTest.TestSerializedRequest(module, "debug_traceBlockByHash", block.Hash, options);
        Assert.That(environments.Created, Is.Zero);

        historyView.Enabled = true;
        string warmed = await RpcTest.TestSerializedRequest(module, "debug_traceBlockByHash", block.Hash, options);

        using JsonDocument expected = JsonDocument.Parse(baseline);
        using JsonDocument actual = JsonDocument.Parse(warmed);
        Assert.That(expected.RootElement.TryGetProperty("error", out _), Is.False);
        Assert.That(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), Is.True);
        Assert.That(environments.Created, Is.InRange(1, 2));
        Assert.That(blockchain.BlockTree.Head!.Header.StateRoot, Is.EqualTo(stateRoot));
        Assert.That(blockchain.StateReader.GetBalance(block.Header, TestItem.AddressA), Is.EqualTo(balance));
        Assert.That(blockchain.StateReader.GetNonce(block.Header, TestItem.AddressA), Is.EqualTo(nonce));
        logger.DidNotReceive().Debug(Arg.Any<string>());

        int createdBeforeOverride = environments.Created;
        await RpcTest.TestSerializedRequest(module, "debug_traceBlockByHash", block.Hash,
            new GethTraceOptions { Tracer = tracer, StateOverrides = [] });
        Assert.That(environments.Created, Is.EqualTo(createdBeforeOverride));
    }

    private sealed class HistoricalBlockView(IBlockTree inner) : BlockTreeTestDouble(inner)
    {
        public bool Enabled { get; set; }
        public override Block? Head { get => Build.A.Block.WithNumber(1_000).TestObject; set { } }
        public override bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) =>
            Enabled && base.IsMainChain(blockHash, throwOnMissingHash);
    }

    private sealed class CountingEnvironmentFactory(IReadOnlyTxProcessingEnvFactory inner) : IReadOnlyTxProcessingEnvFactory
    {
        private int _created;
        public int Created => Volatile.Read(ref _created);

        public IReadOnlyTxProcessorSource Create()
        {
            IReadOnlyTxProcessorSource environment = inner.Create();
            Interlocked.Increment(ref _created);
            return environment;
        }
    }
}
