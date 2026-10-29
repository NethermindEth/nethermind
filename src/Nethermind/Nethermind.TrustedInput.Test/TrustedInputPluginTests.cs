// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using NUnit.Framework;
using Input = Nethermind.Stateless.Execution.IO.TrustedInput;

namespace Nethermind.TrustedInput.Test;

public class TrustedInputPluginTests
{
    [Test]
    public async Task Rpc_returns_the_block_as_a_trusted_input_and_then_serves_it_from_the_store()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Block block = await chain.AddBlock(Transfer(chain));
        TrustedInputRpcModule module = new(new TrustedInputStore(new TrustedInputConfig(), LimboLogs.Instance),
            chain.BlockTree, chain.Bridge, chain.SpecProvider);

        ResultWrapper<byte[]> first = module.trustedinput_getInput(new BlockParameter(block.Number));
        ResultWrapper<byte[]> second = module.trustedinput_getInput(new BlockParameter(block.Hash!));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Result.ResultType, Is.EqualTo(ResultType.Success), first.Result.Error);
            Assert.That(Keccak.Compute(Input.BlockHeaderOf(first.Data)), Is.EqualTo(block.Hash));
            Assert.That(Input.ChainIdOf(first.Data), Is.EqualTo(chain.SpecProvider.ChainId));
            Assert.That(second.Data, Is.SameAs(first.Data));
        }
    }

    [Test]
    public async Task Rpc_rejects_the_genesis_block()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        TrustedInputRpcModule module = new(new TrustedInputStore(new TrustedInputConfig(), LimboLogs.Instance),
            chain.BlockTree, chain.Bridge, chain.SpecProvider);

        Assert.That(module.trustedinput_getInput(new BlockParameter(0UL)).Result.ResultType, Is.EqualTo(ResultType.Failure));
    }

    [Test]
    public async Task Processed_block_is_captured_as_it_is_imported()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(builder => builder
            .AddSingleton<ITrustedInputConfig>(new TrustedInputConfig { Enabled = true })
            .AddSingleton<WitnessCapturingBlockProcessingEnv>()
            .AddModule(new TrustedInputModule()));

        Block block = await chain.AddBlock(Transfer(chain));

        TrustedInputStore store = chain.Container.Resolve<TrustedInputStore>();
        // Captured off the import path.
        Assert.That(() => store.TryGet(block.Hash!, out _), Is.True.After(10_000, 50));
        store.TryGet(block.Hash!, out byte[] input);
        Assert.That(Keccak.Compute(Input.BlockHeaderOf(input)), Is.EqualTo(block.Hash));
    }

    private static Transaction Transfer(TestRpcBlockchain chain) =>
        Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(1).WithNonce(chain.ReadOnlyState.GetNonce(TestItem.AddressA))
            .WithGasLimit(21_000).WithMaxFeePerGas(20.GWei).WithType(TxType.EIP1559)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
}
