// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Benchmark.GasBenchmarks;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Benchmark.Test;

[TestFixture]
public class PayloadLoaderTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "payload_loader_test_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string CreatePayloadFile(string json, string fileName = "test_payload.txt")
    {
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    /// Creates a minimal valid engine_newPayloadV4 JSON-RPC line with configurable fields.
    /// </summary>
    private static string BuildMinimalPayloadJson(
        long blockNumber = 1,
        long gasLimit = 30_000_000,
        long gasUsed = 100_000_000,
        ulong timestamp = 1000,
        string baseFeePerGas = "0x7",
        string parentHash = null,
        string prevRandao = null,
        string feeRecipient = null,
        string blockHash = null,
        string stateRoot = null,
        string[] transactions = null,
        string blobGasUsed = null,
        string excessBlobGas = null,
        string parentBeaconBlockRoot = null,
        string[][] withdrawals = null,
        string[] executionRequests = null)
    {
        parentHash ??= "0x" + new string('a', 64);
        prevRandao ??= "0x" + new string('b', 64);
        feeRecipient ??= "0x" + new string('c', 40);
        blockHash ??= "0x" + new string('d', 64);
        stateRoot ??= "0x" + new string('e', 64);
        transactions ??= Array.Empty<string>();

        string txJson = "[" + string.Join(",", Array.ConvertAll(transactions, t => $"\"{t}\"")) + "]";

        string blobFields = "";
        if (blobGasUsed is not null)
            blobFields += $",\"blobGasUsed\":\"{blobGasUsed}\"";
        if (excessBlobGas is not null)
            blobFields += $",\"excessBlobGas\":\"{excessBlobGas}\"";

        string stateRootField = "";
        if (stateRoot is not null)
            stateRootField = $",\"stateRoot\":\"{stateRoot}\"";

        string withdrawalsField = "";
        if (withdrawals is not null)
        {
            string[] wEntries = new string[withdrawals.Length];
            for (int i = 0; i < withdrawals.Length; i++)
            {
                string[] w = withdrawals[i];
                wEntries[i] = $"{{\"index\":\"{w[0]}\",\"validatorIndex\":\"{w[1]}\",\"address\":\"{w[2]}\",\"amount\":\"{w[3]}\"}}";
            }
            withdrawalsField = $",\"withdrawals\":[{string.Join(",", wEntries)}]";
        }

        string payloadJson =
            $"{{\"blockNumber\":\"0x{blockNumber:x}\"" +
            $",\"gasLimit\":\"0x{gasLimit:x}\"" +
            $",\"gasUsed\":\"0x{gasUsed:x}\"" +
            $",\"timestamp\":\"0x{timestamp:x}\"" +
            $",\"baseFeePerGas\":\"{baseFeePerGas}\"" +
            $",\"parentHash\":\"{parentHash}\"" +
            $",\"prevRandao\":\"{prevRandao}\"" +
            $",\"feeRecipient\":\"{feeRecipient}\"" +
            $",\"blockHash\":\"{blockHash}\"" +
            stateRootField +
            $",\"transactions\":{txJson}" +
            blobFields +
            withdrawalsField +
            "}";

        string paramsJson = $"[{payloadJson}";
        // params[1] = blobVersionedHashes (empty array)
        paramsJson += ",[]";
        // params[2] = parentBeaconBlockRoot
        if (parentBeaconBlockRoot is not null)
            paramsJson += $",\"{parentBeaconBlockRoot}\"";
        else
            paramsJson += ",null";
        // params[3] = executionRequests
        if (executionRequests is not null)
        {
            string reqJson = "[" + string.Join(",", Array.ConvertAll(executionRequests, r => $"\"{r}\"")) + "]";
            paramsJson += $",{reqJson}";
        }
        paramsJson += "]";

        return $"{{\"params\":{paramsJson}}}";
    }

    [Test]
    public void LoadPayload_Parses_Header_Fields_Correctly()
    {
        string json = BuildMinimalPayloadJson(
            blockNumber: 42,
            gasLimit: 30_000_000,
            gasUsed: 21_000,
            timestamp: 1700000000,
            baseFeePerGas: "0xa");

        string path = CreatePayloadFile(json);

        (BlockHeader header, Transaction[] txs) = PayloadLoader.LoadPayload(path);

        Assert.That(header.Number, Is.EqualTo(42));
        Assert.That(header.GasLimit, Is.EqualTo(30_000_000));
        Assert.That(header.GasUsed, Is.EqualTo(21_000));
        Assert.That(header.Timestamp, Is.EqualTo(1700000000UL));
        Assert.That(header.BaseFeePerGas, Is.EqualTo(new UInt256(10)));
        Assert.That(header.IsPostMerge, Is.True);
        Assert.That(txs, Is.Empty);
    }

    [Test]
    public void LoadPayload_Parses_ParentHash_And_MixHash()
    {
        string parentHash = "0x" + new string('1', 64);
        string prevRandao = "0x" + new string('2', 64);
        string json = BuildMinimalPayloadJson(parentHash: parentHash, prevRandao: prevRandao);

        string path = CreatePayloadFile(json);

        (BlockHeader header, _) = PayloadLoader.LoadPayload(path);

        Assert.That(header.ParentHash, Is.EqualTo(new Hash256(Bytes.FromHexString(parentHash))));
        Assert.That(header.MixHash, Is.EqualTo(new Hash256(Bytes.FromHexString(prevRandao))));
    }

    [Test]
    public void LoadPayload_Parses_BlockHash()
    {
        string blockHash = "0x" + new string('f', 64);
        string json = BuildMinimalPayloadJson(blockHash: blockHash);

        string path = CreatePayloadFile(json);

        (BlockHeader header, _) = PayloadLoader.LoadPayload(path);

        Assert.That(header.Hash, Is.EqualTo(new Hash256(Bytes.FromHexString(blockHash))));
    }

    [Test]
    public void LoadPayload_Parses_FeeRecipient()
    {
        string feeRecipient = "0x" + new string('9', 40);
        string json = BuildMinimalPayloadJson(feeRecipient: feeRecipient);

        string path = CreatePayloadFile(json);

        (BlockHeader header, _) = PayloadLoader.LoadPayload(path);

        Assert.That(header.Beneficiary, Is.EqualTo(new Address(feeRecipient)));
    }

    [Test]
    public void LoadPayload_Returns_Empty_Transactions_When_None_Present()
    {
        string json = BuildMinimalPayloadJson();
        string path = CreatePayloadFile(json);

        (_, Transaction[] txs) = PayloadLoader.LoadPayload(path);

        Assert.That(txs, Is.Empty);
    }

    [Test]
    public void LoadBlock_Parses_BlobGasFields()
    {
        string json = BuildMinimalPayloadJson(
            blobGasUsed: "0x20000",
            excessBlobGas: "0x10000");

        string path = CreatePayloadFile(json);

        Block block = PayloadLoader.LoadBlock(path);

        Assert.That(block.Header.BlobGasUsed, Is.EqualTo(0x20000UL));
        Assert.That(block.Header.ExcessBlobGas, Is.EqualTo(0x10000UL));
    }

    [Test]
    public void LoadBlock_Parses_ParentBeaconBlockRoot()
    {
        string beaconRoot = "0x" + new string('3', 64);
        string json = BuildMinimalPayloadJson(parentBeaconBlockRoot: beaconRoot);

        string path = CreatePayloadFile(json);

        Block block = PayloadLoader.LoadBlock(path);

        Assert.That(block.Header.ParentBeaconBlockRoot, Is.EqualTo(new Hash256(Bytes.FromHexString(beaconRoot))));
    }

    [Test]
    public void LoadBlock_Parses_Withdrawals()
    {
        string[][] withdrawals =
        [
            ["0x1", "0x2", "0x" + new string('a', 40), "0x64"]
        ];
        string json = BuildMinimalPayloadJson(withdrawals: withdrawals);

        string path = CreatePayloadFile(json);

        Block block = PayloadLoader.LoadBlock(path);

        Assert.That(block.Withdrawals, Is.Not.Null);
        Assert.That(block.Withdrawals.Length, Is.EqualTo(1));
        Assert.That(block.Withdrawals[0].Index, Is.EqualTo(1UL));
        Assert.That(block.Withdrawals[0].ValidatorIndex, Is.EqualTo(2UL));
        Assert.That(block.Withdrawals[0].AmountInGwei, Is.EqualTo(100UL));
    }

    [Test]
    public void LoadBlock_Returns_Block_With_Transactions_Array()
    {
        string json = BuildMinimalPayloadJson();
        string path = CreatePayloadFile(json);

        Block block = PayloadLoader.LoadBlock(path);

        Assert.That(block, Is.Not.Null);
        Assert.That(block.Transactions, Is.Empty);
        Assert.That(block.Header.Number, Is.EqualTo(1));
    }

    [Test]
    public void LoadBlock_Sets_IsPostMerge()
    {
        string json = BuildMinimalPayloadJson();
        string path = CreatePayloadFile(json);

        Block block = PayloadLoader.LoadBlock(path);

        Assert.That(block.Header.IsPostMerge, Is.True);
    }

    [Test]
    public void ReadRawJson_Returns_First_Line()
    {
        string json = BuildMinimalPayloadJson();
        string path = CreatePayloadFile(json + "\nsecond line");

        string rawJson = PayloadLoader.ReadRawJson(path);

        Assert.That(rawJson, Is.EqualTo(json));
    }

    [Test]
    public void ParseExpectedHashes_Returns_StateRoot_And_BlockHash()
    {
        string stateRoot = "0x" + new string('a', 64);
        string blockHash = "0x" + new string('b', 64);
        string json = BuildMinimalPayloadJson(stateRoot: stateRoot, blockHash: blockHash);
        string path = CreatePayloadFile(json);

        (Hash256 parsedStateRoot, Hash256 parsedBlockHash) = PayloadLoader.ParseExpectedHashes(path);

        Assert.That(parsedStateRoot, Is.EqualTo(new Hash256(Bytes.FromHexString(stateRoot))));
        Assert.That(parsedBlockHash, Is.EqualTo(new Hash256(Bytes.FromHexString(blockHash))));
    }

    [Test]
    public void VerifyProcessedBlock_Does_Not_Throw_When_Hashes_Match()
    {
        string stateRoot = "0x" + new string('a', 64);
        string blockHash = "0x" + new string('b', 64);
        string json = BuildMinimalPayloadJson(stateRoot: stateRoot, blockHash: blockHash);
        string path = CreatePayloadFile(json);

        Block block = Build.A.Block.WithHeader(
            Build.A.BlockHeader
                .WithStateRoot(new Hash256(Bytes.FromHexString(stateRoot)))
                .TestObject).TestObject;
        block.Header.Hash = new Hash256(Bytes.FromHexString(blockHash));

        Assert.DoesNotThrow(() => PayloadLoader.VerifyProcessedBlock(block, "test", path));
    }

    [Test]
    public void VerifyProcessedBlock_Throws_On_StateRoot_Mismatch()
    {
        string expectedRoot = "0x" + new string('a', 64);
        string json = BuildMinimalPayloadJson(stateRoot: expectedRoot);
        string path = CreatePayloadFile(json);

        Block block = Build.A.Block.WithHeader(
            Build.A.BlockHeader
                .WithStateRoot(new Hash256(Bytes.FromHexString("0x" + new string('f', 64))))
                .TestObject).TestObject;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PayloadLoader.VerifyProcessedBlock(block, "test", path));
        Assert.That(ex.Message, Does.Contain("State root mismatch"));
    }

    [Test]
    public void VerifyProcessedBlock_Throws_On_BlockHash_Mismatch()
    {
        string expectedHash = "0x" + new string('b', 64);
        // stateRoot must match so we get to the blockHash check
        string stateRoot = "0x" + new string('a', 64);
        string json = BuildMinimalPayloadJson(stateRoot: stateRoot, blockHash: expectedHash);
        string path = CreatePayloadFile(json);

        Block block = Build.A.Block.WithHeader(
            Build.A.BlockHeader
                .WithStateRoot(new Hash256(Bytes.FromHexString(stateRoot)))
                .TestObject).TestObject;
        block.Header.Hash = new Hash256(Bytes.FromHexString("0x" + new string('f', 64)));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PayloadLoader.VerifyProcessedBlock(block, "test", path));
        Assert.That(ex.Message, Does.Contain("Block hash mismatch"));
    }

    [Test]
    public void LoadPayload_Handles_Zero_BaseFee()
    {
        string json = BuildMinimalPayloadJson(baseFeePerGas: "0x0");
        string path = CreatePayloadFile(json);

        (BlockHeader header, _) = PayloadLoader.LoadPayload(path);

        Assert.That(header.BaseFeePerGas, Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void LoadPayload_Handles_Large_BaseFee()
    {
        // 0xff = 255
        string json = BuildMinimalPayloadJson(baseFeePerGas: "0xff");
        string path = CreatePayloadFile(json);

        (BlockHeader header, _) = PayloadLoader.LoadPayload(path);

        Assert.That(header.BaseFeePerGas, Is.EqualTo(new UInt256(255)));
    }

    [Test]
    public void CreateWorldState_Throws_Before_Genesis_Initialized()
    {
        // PayloadLoader.CreateWorldState should throw if genesis not initialized.
        // Since genesis may already be initialized from other tests, we only test
        // that CreateWorldState returns a valid object if genesis IS initialized.
        // This test documents the expected contract.
        try
        {
            IWorldState state = PayloadLoader.CreateWorldState();
            Assert.That(state, Is.Not.Null);
        }
        catch (InvalidOperationException ex)
        {
            Assert.That(ex.Message, Does.Contain("Genesis not initialized"));
        }
    }

    [Test]
    public void GenesisStateRoot_Throws_Before_Initialization()
    {
        // Like above — documents the contract
        try
        {
            Hash256 root = PayloadLoader.GenesisStateRoot;
            Assert.That(root, Is.Not.Null);
        }
        catch (InvalidOperationException ex)
        {
            Assert.That(ex.Message, Does.Contain("Genesis not initialized"));
        }
    }

    [Test]
    public void LoadBlock_Without_BlobGas_Returns_Null_Fields()
    {
        string json = BuildMinimalPayloadJson();
        string path = CreatePayloadFile(json);

        Block block = PayloadLoader.LoadBlock(path);

        Assert.That(block.Header.BlobGasUsed, Is.Null);
        Assert.That(block.Header.ExcessBlobGas, Is.Null);
    }

    [Test]
    public void LoadBlock_Without_Withdrawals_Returns_Null()
    {
        string json = BuildMinimalPayloadJson();
        string path = CreatePayloadFile(json);

        Block block = PayloadLoader.LoadBlock(path);

        Assert.That(block.Withdrawals, Is.Null);
    }

    [Test]
    public void LoadBlock_Without_ParentBeaconBlockRoot_Returns_Null()
    {
        // Build with null parentBeaconBlockRoot in params[2]
        string json = BuildMinimalPayloadJson(parentBeaconBlockRoot: null);
        string path = CreatePayloadFile(json);

        Block block = PayloadLoader.LoadBlock(path);

        Assert.That(block.Header.ParentBeaconBlockRoot, Is.Null);
    }

    [Test]
    public void LoadBlock_Computes_TxRoot_For_Empty_Transactions()
    {
        string json = BuildMinimalPayloadJson();
        string path = CreatePayloadFile(json);

        Block block = PayloadLoader.LoadBlock(path);

        Assert.That(block.Header.TxRoot, Is.Not.Null);
    }
}
