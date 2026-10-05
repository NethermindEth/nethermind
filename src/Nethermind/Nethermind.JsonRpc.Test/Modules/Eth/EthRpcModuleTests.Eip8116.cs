// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    private const int Eip8116TxsPerBlock = 2;
    private const int Eip8116LogsPerTx = 2;

    private static readonly ulong Eip8116ForkTimestamp =
        (ulong)new DateTimeOffset(TestBlockchain.InitialTimestamp).ToUnixTimeSeconds() + (ulong)TimeSpan.FromDays(1).TotalSeconds;

    private static readonly byte[] TwoLogsInitCode = Prepare.EvmCode
        .PushData(0).PushData(0).Op(Instruction.LOG0)
        .PushData(0).PushData(0).Op(Instruction.LOG0)
        .Done;

    /// <summary>
    /// A block before the EIP-8116 fork and one after it, each with two log-emitting transactions. With the EIP never
    /// scheduled both blocks keep the cumulative receipt gas and block-wide logIndex.
    /// </summary>
    [Test]
    public async Task Eip8116_receipts_and_logs_across_the_fork([Values] bool eip8116Scheduled, [Values] bool parallelExecution)
    {
        ISpecProvider specProvider = eip8116Scheduled
            ? new CustomSpecProvider(
                ((ForkActivation)0, Bogota.Instance),
                (ForkActivation.TimestampOnly(Eip8116ForkTimestamp), new OverridableReleaseSpec(Bogota.Instance) { IsEip8116Enabled = true }))
            : new TestSpecProvider(Bogota.Instance);

        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { Timeout = -1 })
            .WithBlocksConfig(new BlocksConfig { ParallelExecution = parallelExecution })
            .Build(specProvider);

        using JsonDocument filter = JsonDocument.Parse(await chain.TestEthRpc("eth_newFilter", new { fromBlock = "latest" }));
        string filterId = filter.RootElement.GetProperty("result").GetString()!;

        Block preFork = await AddLogsBlock(chain);
        chain.Timestamper.Add(TimeSpan.FromDays(2));
        Block postFork = await AddLogsBlock(chain);

        Assert.That(specProvider.GetSpec(preFork.Header).IsEip8116Enabled, Is.False);
        Assert.That(specProvider.GetSpec(postFork.Header).IsEip8116Enabled, Is.EqualTo(eip8116Scheduled));

        await AssertEip8116Block(chain, preFork, perTx: false);
        await AssertEip8116Block(chain, postFork, perTx: eip8116Scheduled);

        long[] expectedLogIndexes = [.. ExpectedLogIndexes(perTx: false), .. ExpectedLogIndexes(perTx: eip8116Scheduled)];
        using JsonDocument logs = JsonDocument.Parse(await chain.TestEthRpc("eth_getLogs",
            new { fromBlock = preFork.Number.ToHexString(true), toBlock = postFork.Number.ToHexString(true) }));
        using JsonDocument changes = JsonDocument.Parse(await chain.TestEthRpc("eth_getFilterChanges", filterId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LogIndexes(logs.RootElement.GetProperty("result")), Is.EqualTo(expectedLogIndexes), "eth_getLogs");
            Assert.That(LogIndexes(changes.RootElement.GetProperty("result")), Is.EqualTo(expectedLogIndexes), "eth_getFilterChanges");
        }
    }

    private static async Task<Block> AddLogsBlock(TestRpcBlockchain chain)
    {
        ulong nonce = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Transaction[] txs = new Transaction[Eip8116TxsPerBlock];
        for (int i = 0; i < txs.Length; i++)
        {
            txs[i] = Build.A.Transaction
                .WithCode(TwoLogsInitCode)
                .WithValue(0)
                .WithNonce(nonce + (ulong)i)
                .WithGasLimit(1_000_000)
                .WithMaxFeePerGas(20.GWei).WithMaxPriorityFeePerGas(1.GWei).WithType(TxType.EIP1559)
                .WithChainId(TestBlockchainIds.ChainId)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject;
        }

        Block block = await chain.AddBlock(txs);
        Assert.That(block.Transactions.Select(static tx => tx.Hash), Is.EqualTo(txs.Select(static tx => tx.Hash)));
        return block;
    }

    private static long[] ExpectedLogIndexes(bool perTx) =>
        [.. Enumerable.Range(0, Eip8116TxsPerBlock * Eip8116LogsPerTx).Select(i => (long)(perTx ? i % Eip8116LogsPerTx : i))];

    private static long[] LogIndexes(JsonElement logs) =>
        [.. logs.EnumerateArray().Select(static log => Convert.ToInt64(log.GetProperty("logIndex").GetString(), 16))];

    private static async Task AssertEip8116Block(TestRpcBlockchain chain, Block block, bool perTx)
    {
        using JsonDocument blockReceipts = JsonDocument.Parse(await chain.TestEthRpc("eth_getBlockReceipts", block.Number.ToHexString(true)));
        JsonElement[] rpcReceipts = [.. blockReceipts.RootElement.GetProperty("result").EnumerateArray()];
        TxReceipt[] stored = chain.ReceiptStorage.Get(block);
        IReceiptSpec spec = chain.SpecProvider.GetSpec(block.Header);

        ulong cumulative = 0;
        TxReceipt[] expected = new TxReceipt[rpcReceipts.Length];
        for (int i = 0; i < rpcReceipts.Length; i++)
        {
            using JsonDocument single = JsonDocument.Parse(await chain.TestEthRpc("eth_getTransactionReceipt", block.Transactions[i].Hash!.ToString()));
            JsonElement receipt = single.RootElement.GetProperty("result");
            ulong gasUsed = Convert.ToUInt64(receipt.GetProperty("gasUsed").GetString(), 16);
            cumulative += gasUsed;
            ulong receiptGasField = perTx ? gasUsed : cumulative;
            expected[i] = new TxReceipt
            {
                TxType = block.Transactions[i].Type,
                StatusCode = StatusCode.Success,
                GasUsedTotal = receiptGasField,
                Logs = stored[i].Logs,
                Bloom = new Bloom(stored[i].Logs!),
            };

            using (Assert.EnterMultipleScope())
            {
                Assert.That(receipt.GetProperty("status").GetString(), Is.EqualTo("0x1"), $"tx {i} status");
                Assert.That(Convert.ToUInt64(receipt.GetProperty("cumulativeGasUsed").GetString(), 16), Is.EqualTo(receiptGasField), $"tx {i} cumulativeGasUsed");
                Assert.That(LogIndexes(receipt.GetProperty("logs")), Is.EqualTo(ExpectedLogIndexes(perTx).Skip(i * Eip8116LogsPerTx).Take(Eip8116LogsPerTx)), $"tx {i} logIndex");
                Assert.That(rpcReceipts[i].GetRawText(), Is.EqualTo(receipt.GetRawText()), $"tx {i} eth_getBlockReceipts");
                Assert.That(stored[i].GasUsedTotal, Is.EqualTo(receiptGasField), $"tx {i} stored receipt gas field");
            }

            ReceiptWithProof proof = chain.ProofRpcModule.proof_getTransactionReceipt(block.Transactions[i].Hash!, false).Data!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Keccak.Compute(proof.ReceiptProof[0]), Is.EqualTo(block.Header.ReceiptsRoot), $"tx {i} receipt proof root");
                Assert.That(proof.Receipt.CumulativeGasUsed, Is.EqualTo(receiptGasField), $"tx {i} proven cumulativeGasUsed");
                Assert.That(proof.Receipt.Logs!.Select(static log => log.LogIndex!.Value), Is.EqualTo(ExpectedLogIndexes(perTx).Skip(i * Eip8116LogsPerTx).Take(Eip8116LogsPerTx)), $"tx {i} proven logIndex");
            }
        }

        Hash256 expectedRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(expected, spec, null);
        Assert.That(block.Header.ReceiptsRoot, Is.EqualTo(expectedRoot), "receipts root");
        AssertReceiptRlpRoundTrip(stored);
    }

    private static void AssertReceiptRlpRoundTrip(TxReceipt[] receipts)
    {
        foreach (TxReceipt receipt in receipts)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(RoundTrip(new ReceiptMessageDecoder(), receipt, RlpBehaviors.Eip658Receipts).GasUsedTotal, Is.EqualTo(receipt.GasUsedTotal), "consensus RLP");
                Assert.That(RoundTrip(new ReceiptMessageDecoder69(), receipt, RlpBehaviors.Eip658Receipts).GasUsedTotal, Is.EqualTo(receipt.GasUsedTotal), "eth/69 RLP");
                Assert.That(RoundTrip(new CompactReceiptStorageDecoder(), receipt, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts).GasUsedTotal, Is.EqualTo(receipt.GasUsedTotal), "storage RLP");
            }
        }

        static TxReceipt RoundTrip(RlpDecoder<TxReceipt> decoder, TxReceipt receipt, RlpBehaviors behaviors)
        {
            RlpReader reader = new(decoder.EncodeAsBytes(receipt, behaviors));
            return decoder.Decode(ref reader, behaviors)!;
        }
    }
}
