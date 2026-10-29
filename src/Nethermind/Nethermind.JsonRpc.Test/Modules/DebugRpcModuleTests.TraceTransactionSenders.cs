// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.History.Changesets;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using FlatHistoryColumns = Nethermind.State.Flat.FlatHistoryColumns;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    private const int SenderTestTransactions = 5;

    /// <remarks>The block trace runs every transaction, which makes its entry the reference for the target.</remarks>
    [Test]
    public async Task Single_transaction_trace_recovers_senders_only_up_to_its_target(
        [Values("debug_traceTransaction", "trace_transaction")] string method,
        [Values(0, 2, SenderTestTransactions - 1)] int target)
    {
        RecoveryCount recoveries = new();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder.AddDecorator<IEthereumEcdsa>((_, inner) => new RecoveryCountingEcdsa(inner, recoveries)));
        Block block = await chain.AddBlock(LegacyTransfers(chain, SenderTestTransactions));
        Assert.That(block.Transactions, Has.Length.EqualTo(SenderTestTransactions), "precondition: every transaction is included");

        JToken fromBlock = await TraceInBlock(chain, method, block, target);
        Transaction[] traced = ClearSenders(chain, block);
        recoveries.Reset();
        JToken single = await TraceSingle(chain, method, block.Transactions[target].Hash!);
        int recovered = recoveries.Count;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered, Is.EqualTo(target + 1), "the replay runs the prefix and the target, nothing after it");
            for (int i = 0; i < traced.Length; i++)
            {
                Assert.That(traced[i].SenderAddress, i <= target ? Is.EqualTo(TestItem.AddressB) : Is.Null, $"sender of transaction {i}");
            }
            Assert.That(JToken.DeepEquals(single, fromBlock), Is.True, $"{single}{Environment.NewLine}{fromBlock}");
        }
    }

    [Test]
    public async Task Seeded_single_transaction_trace_recovers_only_the_target_sender(
        [Values("debug_traceTransaction", "trace_transaction")] string method)
    {
        const int target = 2;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        RecoveryCount recoveries = new();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton<IPrefixStateSeedSource>(new ChangesetPrefixStateSeedSource(index))
                .AddDecorator<IEthereumEcdsa>((_, inner) => new RecoveryCountingEcdsa(inner, recoveries)));
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await chain.AddBlock(LegacyTransfers(chain, SenderTestTransactions));
        Assert.That(block.Transactions, Has.Length.EqualTo(SenderTestTransactions), "precondition: every transaction is included");
        Hash256 txHash = block.Transactions[target].Hash!;

        JToken replayed = await TraceSingle(chain, method, txHash);
        IndexThroughTheCapture(chain, index, block, parent);
        Transaction[] traced = ClearSenders(chain, block);
        recoveries.Reset();
        JToken seeded = await TraceSingle(chain, method, txHash);
        int recovered = recoveries.Count;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered, Is.EqualTo(1), "a seeded prefix is not executed, so only the target needs its sender");
            for (int i = 0; i < traced.Length; i++)
            {
                Assert.That(traced[i].SenderAddress, i == target ? Is.EqualTo(TestItem.AddressB) : Is.Null, $"sender of transaction {i}");
            }
            Assert.That(JToken.DeepEquals(seeded, replayed), Is.True, $"{seeded}{Environment.NewLine}{replayed}");
        }
    }

    /// <remarks>
    /// The first transaction is made to hash like the target. A lookup by hash stops there and traces it; the index the
    /// request names does not. The transfers carry different values, so the two traces differ.
    /// </remarks>
    [Test]
    public async Task Trace_by_block_and_index_uses_the_index_instead_of_a_hash_lookup()
    {
        const int target = 2;
        using SnapshotableMemColumnsDb<FlatHistoryColumns> columns = new();
        TransactionChangesetIndex index = new(columns, new FlatDbConfig { HistoryTransactionIndexEnabled = true });
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddSingleton<IPrefixStateSeedSource>(new ChangesetPrefixStateSeedSource(index)));
        ulong nonce = chain.ReadOnlyState.GetNonce(TestItem.AddressB);
        Transaction[] transfers = new Transaction[SenderTestTransactions];
        for (int i = 0; i < transfers.Length; i++)
        {
            transfers[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i).WithValue((UInt256)(i + 1))
                .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        }
        BlockHeader parent = chain.BlockTree.Head!.Header;
        Block block = await chain.AddBlock(transfers);
        Assert.That(block.Transactions, Has.Length.EqualTo(SenderTestTransactions), "precondition: every transaction is included");
        GethTraceOptions callTracer = new() { Tracer = NativeCallTracer.CallTracer };

        JToken expected = await TraceByIndex(chain, block, target, callTracer);
        IndexThroughTheCapture(chain, index, block, parent);
        Transaction[] stored = chain.BlockTree.FindBlock(block.Hash!)!.Transactions;
        stored[0].SetPreHash(Rlp.Encode(stored[target], RlpBehaviors.SkipTypedWrapping).Bytes);
        Assert.That(stored[0].Hash, Is.EqualTo(stored[target].Hash), "precondition: the first transaction hashes like the target");
        JToken seeded = await TraceByIndex(chain, block, target, callTracer);

        Assert.That(JToken.DeepEquals(seeded, expected), Is.True, $"{seeded}{Environment.NewLine}{expected}");
    }

    private static async Task<JToken> TraceByIndex(TestRpcBlockchain chain, Block block, int index, GethTraceOptions options)
    {
        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceTransactionByBlockhashAndIndex", block.Hash!, index, options);
        JToken result = JToken.Parse(response)["result"]!;
        Assert.That(result.Type, Is.Not.EqualTo(JTokenType.Null), response);
        return result;
    }

    [Test]
    public async Task Single_transaction_trace_recovers_authorities_only_of_executed_transactions(
        [Values("debug_traceTransaction", "trace_transaction")] string method,
        [Values(0, 1)] int target)
    {
        RecoveryCount recoveries = new();
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(builder => builder
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
                .AddDecorator<IEthereumEcdsa>((_, inner) => new RecoveryCountingEcdsa(inner, recoveries)));
        ulong nonce = chain.ReadOnlyState.GetNonce(TestItem.AddressB);
        ulong chainId = chain.SpecProvider.ChainId;
        AuthorizationTuple authorization = new EthereumEcdsa(chainId).Sign(TestItem.PrivateKeyC, chainId, TestItem.AddressD,
            chain.ReadOnlyState.GetNonce(TestItem.AddressC));
        Transaction[] transactions =
        [
            Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce).WithValue(1).WithGasLimit(100_000)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject,
            Build.A.Transaction.WithType(TxType.SetCode).WithChainId(chainId).WithTo(TestItem.AddressE).WithNonce(nonce + 1)
                .WithMaxFeePerGas(1.GWei).WithMaxPriorityFeePerGas(1).WithGasLimit(100_000).WithAuthorizationCode(authorization)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject,
        ];
        Block block = await chain.AddBlock(transactions);
        Assert.That(block.Transactions, Has.Length.EqualTo(2), "precondition: both transactions are included");

        JToken fromBlock = await TraceInBlock(chain, method, block, target);
        Transaction[] traced = ClearSenders(chain, block);
        recoveries.Reset();
        JToken single = await TraceSingle(chain, method, block.Transactions[target].Hash!);
        int recovered = recoveries.Count;

        using (Assert.EnterMultipleScope())
        {
            AuthorizationTuple tracedAuthorization = traced[1].AuthorizationList![0];
            if (target == 0)
            {
                Assert.That(recovered, Is.EqualTo(1), "neither the sender nor the authority after the target is recovered");
                Assert.That(traced[1].SenderAddress, Is.Null);
                Assert.That(tracedAuthorization.Authority, Is.Null);
            }
            else
            {
                // The set-code sender may come from the process-wide sender cache; its authority never does.
                Assert.That(recovered, Is.InRange(2, 3));
                Assert.That(traced[1].SenderAddress, Is.EqualTo(TestItem.AddressB));
                Assert.That(tracedAuthorization.Authority, Is.EqualTo(TestItem.AddressC));
            }
            Assert.That(JToken.DeepEquals(single, fromBlock), Is.True, $"{single}{Environment.NewLine}{fromBlock}");
        }
    }

    private static Transaction[] LegacyTransfers(TestRpcBlockchain chain, int count)
    {
        ulong nonce = chain.ReadOnlyState.GetNonce(TestItem.AddressB);
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < transactions.Length; i++)
        {
            // Legacy transactions bypass the process-wide sender cache, so every recovery reaches the counter.
            transactions[i] = Build.A.Transaction.WithTo(TestItem.AddressC).WithNonce(nonce + (ulong)i).WithValue(1)
                .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        }

        return transactions;
    }

    private static async Task<JToken> TraceSingle(TestRpcBlockchain chain, string method, Hash256 txHash)
    {
        string response = method == "trace_transaction"
            ? await RpcTest.TestSerializedRequest(chain.TraceRpcModule, method, txHash)
            : await RpcTest.TestSerializedRequest(chain.DebugRpcModule, method, txHash);
        JToken result = JToken.Parse(response)["result"]!;
        Assert.That(result.Type, Is.Not.EqualTo(JTokenType.Null), response);
        return result;
    }

    private static async Task<JToken> TraceInBlock(TestRpcBlockchain chain, string method, Block block, int index)
    {
        if (method == "trace_transaction")
        {
            string traces = await RpcTest.TestSerializedRequest(chain.TraceRpcModule, "trace_block", block.Number);
            return new JArray(JToken.Parse(traces)["result"]!.Where(trace => (int?)trace["transactionPosition"] == index));
        }

        string response = await RpcTest.TestSerializedRequest(chain.DebugRpcModule, "debug_traceBlockByHash", block.Hash!);
        return JToken.Parse(response)["result"]![index]!["result"]!;
    }

    /// <summary>Clears what block production recovered on the transaction objects the block tree hands to the tracer.</summary>
    private static Transaction[] ClearSenders(TestRpcBlockchain chain, Block block)
    {
        Transaction[] stored = chain.BlockTree.FindBlock(block.Hash!)!.Transactions;
        Assert.That(stored[0], Is.SameAs(chain.BlockTree.FindBlock(block.Hash!)!.Transactions[0]), "precondition: the tracer reads these transactions");
        foreach (Transaction transaction in stored)
        {
            transaction.SenderAddress = null;
            if (transaction.AuthorizationList is { } authorizations)
            {
                foreach (AuthorizationTuple authorization in authorizations) authorization.Authority = null;
            }
        }

        return stored;
    }

    private sealed class RecoveryCount
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);

        public void Reset() => Volatile.Write(ref _count, 0);
    }

    private sealed class RecoveryCountingEcdsa(IEthereumEcdsa inner, RecoveryCount count) : IEthereumEcdsa
    {
        public ulong ChainId => inner.ChainId;

        public Signature Sign(PrivateKey privateKey, in ValueHash256 message) => inner.Sign(privateKey, in message);

        public PublicKey? RecoverPublicKey(Signature signature, in ValueHash256 message) => inner.RecoverPublicKey(signature, in message);

        public CompressedPublicKey? RecoverCompressedPublicKey(Signature signature, in ValueHash256 message) =>
            inner.RecoverCompressedPublicKey(signature, in message);

        public Address? RecoverAddress(Signature signature, in ValueHash256 message)
        {
            count.Increment();
            return inner.RecoverAddress(signature, in message);
        }
    }
}
