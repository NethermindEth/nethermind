// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Follower;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Client;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezSequencerApiTests
{
    [Test]
    [NonParallelizable]
    public async Task RawBlock_DebugOrStandardRpc_PreservesEveryHeaderAndTransactionByte([Values] bool debug, [Values] bool native)
    {
        TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder(NativeSystemTxFormat.ContractV7));
        try
        {
            Block block = Sample(native);
            IJsonRpcClient rpc = Client(block, out _);
            byte[] expected = Rlp.Encode(block).Bytes;
            if (debug) rpc.Post<byte[]?>("debug_getRawBlock", block.Hash!.ToString()).Returns(expected);
            using IContainer container = Container(rpc);
            IEezSequencerApi source = container.Resolve<IEezSequencerApi>();

            byte[]? first = await source.GetRawBlock(block.Hash!, CancellationToken.None);
            byte[]? second = await source.GetRawBlock(block.Hash!, CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first, Is.EqualTo(expected));
                Assert.That(second, Is.EqualTo(expected));
            }
            await rpc.Received(debug ? 2 : 1).Post<byte[]?>("debug_getRawBlock", block.Hash!.ToString());
            await rpc.Received(debug ? 0 : 2).Post<SequencerRpcBlock?>("eth_getBlockByHash", block.Hash!.ToString(), false);
        }
        finally { TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder()); }
    }

    [Test]
    public async Task RawBlock_StandardRpcRejectsMissingOrCorruptedInputs([Values("header", "transaction", "missing-transaction", "invalid-rlp", "null-hash", "withdrawals", "uncles", "missing-hashes")] string mutation)
    {
        Block block = Sample(false);
        IJsonRpcClient rpc = Client(block, out SequencerRpcBlock wire);
        switch (mutation)
        {
            case "header": wire.StateRoot = TestItem.KeccakH; break;
            case "transaction":
                Transaction other = Build.A.Transaction.WithNonce(3).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                rpc.Post<byte[]?>("eth_getRawTransactionByHash", block.Transactions[0].Hash!.ToString())
                    .Returns(TxDecoder.Instance.Encode(other, RlpBehaviors.SkipTypedWrapping).Bytes);
                break;
            case "invalid-rlp":
                byte[] malformed = [0xc0];
                wire.Transactions![0] = Keccak.Compute(malformed);
                rpc.Post<byte[]?>("eth_getRawTransactionByHash", wire.Transactions[0].ToString()).Returns(malformed);
                break;
            case "null-hash": wire.Transactions![0] = null!; break;
            case "missing-transaction": rpc.Post<byte[]?>("eth_getRawTransactionByHash", block.Transactions[0].Hash!.ToString()).Returns((byte[]?)null); break;
            case "withdrawals": wire.Withdrawals = null; break;
            case "uncles": wire.Uncles = [TestItem.KeccakH]; break;
            case "missing-hashes": wire.Transactions = null; break;
        }
        using IContainer container = Container(rpc);
        IEezSequencerApi source = container.Resolve<IEezSequencerApi>();

        await Assert.ThrowsAsync<L1SourceIncompleteException>(() => source.GetRawBlock(block.Hash!, CancellationToken.None));
    }

    [Test]
    public async Task RawBlock_MissingOnBothMethods_RemainsRetryable()
    {
        IJsonRpcClient rpc = Substitute.For<IJsonRpcClient>();
        rpc.Post<byte[]?>("debug_getRawBlock", TestItem.KeccakA.ToString()).Returns((byte[]?)null);
        rpc.Post<SequencerRpcBlock?>("eth_getBlockByHash", TestItem.KeccakA.ToString(), false).Returns((SequencerRpcBlock?)null);
        using IContainer container = Container(rpc);
        IEezSequencerApi source = container.Resolve<IEezSequencerApi>();

        Assert.That(await source.GetRawBlock(TestItem.KeccakA, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task StandardRpc_BoundsConcurrentReadsAndPreservesOrderOrCancellation([Values] bool cancel)
    {
        Block block = Sample(false, 20);
        IJsonRpcClient rpc = Client(block, out _);
        TaskCompletionSource<byte[]?>[] pending = new TaskCompletionSource<byte[]?>[block.Transactions.Length];
        TaskCompletionSource ninth = new(TaskCreationOptions.RunContinuationsAsynchronously);
        for (int index = 0; index < pending.Length; index++)
        {
            int slot = index;
            pending[index] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            rpc.Post<byte[]?>("eth_getRawTransactionByHash", block.Transactions[index].Hash!.ToString()).Returns(_ =>
            {
                if (slot == 8) ninth.TrySetResult();
                return pending[slot].Task;
            });
        }
        using IContainer container = Container(rpc);
        using CancellationTokenSource stop = new();
        Task<byte[]?> result = container.Resolve<IEezSequencerApi>().GetRawBlock(block.Hash!, stop.Token);
        try
        {
            Assert.That(rpc.ReceivedCalls().Count(static call => (string)call.GetArguments()[0]! == "eth_getRawTransactionByHash"), Is.EqualTo(8));
            if (cancel)
            {
                stop.Cancel();
                await Assert.CatchAsync<OperationCanceledException>(async () => await result.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.That(ninth.Task.IsCompleted, Is.False);
            }
            else
            {
                for (int index = 7; index >= 0; index--)
                    pending[index].SetResult(TxDecoder.Instance.Encode(block.Transactions[index], RlpBehaviors.SkipTypedWrapping).Bytes);
                await ninth.Task.WaitAsync(TimeSpan.FromSeconds(10));
                for (int index = pending.Length - 1; index >= 8; index--)
                    pending[index].SetResult(TxDecoder.Instance.Encode(block.Transactions[index], RlpBehaviors.SkipTypedWrapping).Bytes);
                Assert.That(await result.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(Rlp.Encode(block).Bytes));
            }
        }
        finally
        {
            foreach (TaskCompletionSource<byte[]?> request in pending) request.TrySetResult(null);
        }
    }

    private static IContainer Container(IJsonRpcClient rpc)
    {
        EezConfig config = new()
        {
            FollowerEnabled = true,
            SequencerRpcUrl = "http://sequencer.test/",
            L1RpcUrl = "http://l1.test/",
            RegistryAddress = Address.Zero.ToString(),
            RegistryDeployBlock = 1,
            RollupId = 1,
        };
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<IEezConfig>(config)
            .AddModule(new EezModule(config, EezChainRules.Rollup0));
        builder.RegisterInstance(rpc).Keyed<IJsonRpcClient>(EezModule.SequencerRpcClientKey);
        return builder.Build();
    }

    private static IJsonRpcClient Client(Block block, out SequencerRpcBlock wire)
    {
        EthereumJsonSerializer serializer = new();
        BlockForRpc value = new(block, false, new TestSpecProvider(Osaka.Instance)) { SlotNumber = block.Header.SlotNumber };
        wire = serializer.Deserialize<SequencerRpcBlock>(serializer.Serialize(value))!;
        IJsonRpcClient rpc = Substitute.For<IJsonRpcClient>();
        rpc.Post<byte[]?>("debug_getRawBlock", block.Hash!.ToString()).Returns((byte[]?)null);
        rpc.Post<SequencerRpcBlock?>("eth_getBlockByHash", block.Hash!.ToString(), false).Returns(wire);
        foreach (Transaction transaction in block.Transactions)
            rpc.Post<byte[]?>("eth_getRawTransactionByHash", transaction.Hash!.ToString()).Returns(TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes);
        return rpc;
    }

    private static Block Sample(bool native, int count = 1)
    {
        Transaction[] transactions = new Transaction[count];
        for (int index = 0; index < count; index++)
            transactions[index] = native
                ? new NativeHistoryTransaction(NativeHistoryEnvelope.CreateBlocks(1, [new(1, TestItem.KeccakA.ValueHash256)]))
                : Build.A.Transaction.WithNonce((ulong)index + 2).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        foreach (Transaction transaction in transactions)
            transaction.Hash ??= Keccak.Compute(TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes);
        Block block = Build.A.Block.WithNumber(7).WithTransactions(transactions).WithWithdrawals([]).TestObject;
        BlockHeader header = block.Header;
        header.BaseFeePerGas = 7;
        header.BlobGasUsed = 0;
        header.ExcessBlobGas = 1;
        header.ParentBeaconBlockRoot = TestItem.KeccakB;
        header.RequestsHash = TestItem.KeccakC;
        header.BlockAccessListHash = TestItem.KeccakD;
        header.SlotNumber = 42;
        header.Hash = Keccak.Compute(Rlp.Encode(header).Bytes);
        return block;
    }
}
