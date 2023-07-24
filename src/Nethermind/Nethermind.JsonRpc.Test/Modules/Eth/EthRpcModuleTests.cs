// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.TxPool;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReceivedExtensions;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public partial class EthRpcModuleTests
{
    [TestCase("earliest", "0x3635c9adc5dea00000")]
    [TestCase("latest", "0x3635c9adc5de9f09e5")]
    [TestCase("pending", "0x3635c9adc5de9f09e5")]
    [TestCase("0x0", "0x3635c9adc5dea00000")]
    public async Task Eth_get_balance(string blockParameter, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true), blockParameter);
        serialized.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\",\"id\":67}}");
    }

    [Test]
    public async Task Eth_get_balance_default_block()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true));
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x3635c9adc5de9f09e5\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_eth_feeHistory()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_feeHistory", "0x1", "latest", "[20,50,90]");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x0\",\"0x0\"],\"gasUsedRatio\":[0.0105],\"oldestBlock\":\"0x3\",\"reward\":[[\"0x1\",\"0x1\",\"0x1\"]]},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_transaction_by_block_hash_and_index()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getTransactionByBlockHashAndIndex", ctx.Test.BlockTree.FindHeadBlock()!.Hash!.ToString(), "1");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_transaction_by_hash()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getTransactionByHash", ctx.Test.BlockTree.FindHeadBlock()!.Transactions.Last().Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task eth_maxPriorityFeePerGas_test()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_maxPriorityFeePerGas");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x1\",\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_pending_transactions()
    {
        using Context ctx = await Context.Create();
        ctx.Test.AddTransactions(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).TestObject);
        string serialized = ctx.Test.TestEthRpc("eth_pendingTransactions");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[{\"hash\":\"0x190d9a78dbc61b1856162ab909976a1b28ba4a41ee041341576ea69686cd3b29\",\"nonce\":\"0x0\",\"blockHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"blockNumber\":null,\"transactionIndex\":null,\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"type\":\"0x0\",\"v\":\"0x26\",\"s\":\"0x2d04e55699fa32e6b65a22189f7571f5030d636d7d44a8b53fe016a2c3ecde24\",\"r\":\"0xda3978c3a1430bd902cf5bbca73c5a1eca019b3f003c95ee16657fd0bb89534c\"}],\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_pending_transactions_1559_tx()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        ctx.Test.AddTransactions(Build.A.Transaction.WithMaxPriorityFeePerGas(6.GWei()).WithMaxFeePerGas(11.GWei()).WithType(TxType.EIP1559).SignedAndResolved(TestItem.PrivateKeyC).TestObject);
        const string addedTx = "\"hash\":\"0xc668c8940b7416fe06db0dac853210d6d64fb2e9528c439c135a53106517fca6\",\"nonce\":\"0x0\",\"blockHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"blockNumber\":null,\"transactionIndex\":null,\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\",\"gasPrice\":\"0x28fa6ae00\",\"maxPriorityFeePerGas\":\"0x165a0bc00\",\"maxFeePerGas\":\"0x28fa6ae00\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"chainId\":\"0x1\",\"type\":\"0x2\",\"v\":\"0x1\",\"s\":\"0x24e1404423c47d5c5fd9e0b6205811eaa3052f9acdb91a9c08821c2b7a0db1a4\",\"r\":\"0x408e34747109a32b924c61acb879d628505dbd0dcab15a3b1e3a4cfd589b65d2\",\"yParity\":\"0x1\"";
        string serialized = ctx.Test.TestEthRpc("eth_pendingTransactions");
        serialized.Contains(addedTx).Should().BeTrue();
    }

    [Test]
    public async Task Eth_pending_transactions_2930_tx()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        ctx.Test.AddTransactions(Build.A.Transaction.WithMaxPriorityFeePerGas(6.GWei()).WithMaxFeePerGas(11.GWei()).WithType(TxType.AccessList).SignedAndResolved(TestItem.PrivateKeyC).TestObject);
        const string addedTx = "\"hash\":\"0xa296c4cf8ece2d7788e4a71125133dfd8025fc35cc5ffa3e283bc62b027cf512\",\"nonce\":\"0x0\",\"blockHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"blockNumber\":null,\"transactionIndex\":null,\"from\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\",\"gasPrice\":\"0x165a0bc00\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"chainId\":\"0x1\",\"type\":\"0x1\",\"v\":\"0x0\",\"s\":\"0x2b4cbea82cc417cdf510fb5fb0613a2881f2b8a76cd6cce6a5f77872f5124b44\",\"r\":\"0x925ede2e48031b060e6c4a0c7184eb58e37a19b41a3ba15a2d9767c0c41f6d76\",\"yParity\":\"0x0\"";
        string serialized = ctx.Test.TestEthRpc("eth_pendingTransactions");
        serialized.Contains(addedTx).Should().BeTrue();
    }

    [Test]
    public async Task Eth_get_transaction_by_block_number_and_index()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getTransactionByBlockNumberAndIndex", ctx.Test.BlockTree.FindHeadBlock()!.Number.ToString(), "1");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"totalDifficulty\":\"0x0\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"totalDifficulty\":\"0x0\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x0\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    public async Task Eth_get_uncle_by_block_number_and_index(bool eip1559, string expectedJson)
    {
        ISpecProvider? specProvider = null;
        if (eip1559)
        {
            specProvider = Substitute.For<ISpecProvider>();
            ReleaseSpec releaseSpec = new() { IsEip1559Enabled = true, Eip1559TransitionBlock = 0 };
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        }
        using Context ctx = await Context.Create();
        Block block = Build.A.Block.WithUncles(Build.A.BlockHeader.TestObject, Build.A.BlockHeader.TestObject).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindBlock((BlockParameter?)null).ReturnsForAnyArgs(block);
        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockTree).Build(specProvider);
        string serialized = ctx.Test!.TestEthRpc("eth_getUncleByBlockNumberAndIndex", ctx.Test.BlockTree.FindHeadBlock()!.Number.ToString(), "1");
        Assert.That(serialized, Is.EqualTo(expectedJson), serialized?.Replace("\"", "\\\""));
    }

    [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"totalDifficulty\":\"0x0\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"totalDifficulty\":\"0x0\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x0\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    public async Task Eth_get_uncle_by_block_hash_and_index(bool eip1559, string expectedJson)
    {
        ISpecProvider? specProvider = null;
        if (eip1559)
        {
            specProvider = Substitute.For<ISpecProvider>();
            ReleaseSpec releaseSpec = new() { IsEip1559Enabled = true, Eip1559TransitionBlock = 1 };
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        }

        using Context ctx = await Context.Create();
        Block block = Build.A.Block.WithUncles(Build.A.BlockHeader.WithNumber(2).TestObject, Build.A.BlockHeader.TestObject).WithNumber(3).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindBlock((BlockParameter?)null).ReturnsForAnyArgs(block);
        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockTree).Build(specProvider);
        string serialized = ctx.Test.TestEthRpc("eth_getUncleByBlockHashAndIndex", ctx.Test.BlockTree.FindHeadBlock()!.Hash!.ToString(), "1");
        Assert.That(serialized, Is.EqualTo(expectedJson), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_uncle_count_by_block_hash()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getUncleCountByBlockHash", ctx.Test.BlockTree.FindHeadBlock()!.Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_uncle_count_by_block_number()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getUncleCountByBlockNumber", ctx.Test.BlockTree.FindHeadBlock()!.Number.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [TestCase("earliest", "0x0")]
    [TestCase("latest", "0x3")]
    [TestCase("pending", "0x4")]
    [TestCase("0x0", "0x0")]
    public async Task Eth_get_tx_count(string blockParameter, string expectedResult)
    {
        using Context ctx = await Context.Create();

        // Add two transactions, one with the next nonce (nonce=3) and the second one with a gap in nonce (nonce=5, skipping nonce=4)
        Transaction txWithNextNonce = Build.A.Transaction.To(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA).WithValue(0.Ether()).WithNonce(3).TestObject;
        Transaction txWithFutureNonce = Build.A.Transaction.To(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA).WithValue(0.Ether()).WithNonce(5).TestObject;
        ValueTask<(Keccak? Hash, AcceptTxResult? AddTxResult)> resultNextNonce =
            ctx.Test.TxSender.SendTransaction(txWithNextNonce, TxHandlingOptions.None);
        ValueTask<(Keccak? Hash, AcceptTxResult? AddTxResult)> resultFutureNonce =
            ctx.Test.TxSender.SendTransaction(txWithFutureNonce, TxHandlingOptions.None);
        Assert.That(AcceptTxResult.Accepted, Is.EqualTo(resultNextNonce.Result.AddTxResult));
        Assert.That(AcceptTxResult.Accepted, Is.EqualTo(resultFutureNonce.Result.AddTxResult));

        string serialized = ctx.Test.TestEthRpc("eth_getTransactionCount", TestItem.AddressA.Bytes.ToHexString(true), blockParameter);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\",\"id\":67}}"));
    }

    [Test]
    public async Task Eth_get_tx_count_default_block()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getTransactionCount", TestItem.AddressA.Bytes.ToHexString(true));
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x3\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_tx_count_pending_block()
    {
        using Context ctx = await Context.Create();
        string serializedPendingBefore = ctx.Test.TestEthRpc("eth_getTransactionCount", TestItem.AddressB.Bytes.ToHexString(true), "pending");
        Assert.That(serializedPendingBefore, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}"));
        Transaction txWithNextNonce = Build.A.Transaction.To(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyB).WithValue(0.Ether()).WithNonce(0).TestObject;
        ValueTask<(Keccak? Hash, AcceptTxResult? AddTxResult)> resultNextNonce =
            ctx.Test.TxSender.SendTransaction(txWithNextNonce, TxHandlingOptions.None);
        Assert.That(AcceptTxResult.Accepted, Is.EqualTo(resultNextNonce.Result.AddTxResult));
        string serializedLatestAfter = ctx.Test.TestEthRpc("eth_getTransactionCount", TestItem.AddressB.Bytes.ToHexString(true));
        Assert.That(serializedLatestAfter, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}"));
        string serializedPendingAfter = ctx.Test.TestEthRpc("eth_getTransactionCount", TestItem.AddressB.Bytes.ToHexString(true), "pending");
        Assert.That(serializedPendingAfter, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x1\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_changes_empty()
    {
        using Context ctx = await Context.Create();
        _ = ctx.Test.TestEthRpc("eth_newBlockFilter");
        string serialized2 = ctx.Test.TestEthRpc("eth_getFilterChanges", "0");
        Assert.That(serialized2, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_changes_missing()
    {
        using Context ctx = await Context.Create();
        string serialized2 = ctx.Test.TestEthRpc("eth_getFilterChanges", "0");
        Assert.That(serialized2, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"Filter not found\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_uninstall_filter()
    {
        using Context ctx = await Context.Create();
        _ = ctx.Test.TestEthRpc("eth_newBlockFilter");
        string serialized2 = ctx.Test.TestEthRpc("eth_uninstallFilter", "0");
        Assert.That(serialized2, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_changes_with_block()
    {
        using Context ctx = await Context.Create();
        _ = ctx.Test.TestEthRpc("eth_newBlockFilter");
        await ctx.Test.AddBlock();
        string serialized2 = ctx.Test.TestEthRpc("eth_getFilterChanges", "0");

        Assert.That(serialized2, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[\"0x166781de9c5f3328b7fc59c32e1dd1ec892021d95578258004ee221863a817a0\"],\"id\":67}"), serialized2.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_filter_changes_with_log_filter()
    {
        byte[] logCreateCode = Prepare.EvmCode
                .PushData(32)
                .PushData(0)
                .Op(Instruction.LOG0)
                .Done;

        Transaction createCodeTx = Build.A.Transaction
            .SignedAndResolved(TestItem.PrivateKeyA).WithChainId(TestBlockchainIds.ChainId).WithGasPrice(2)
            .WithCode(logCreateCode)
            .WithNonce(3).WithGasLimit(210200).WithGasPrice(20.GWei()).TestObject;

        var test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(initialValues: 2.Ether());

        Keccak? blockHash = Keccak.Zero;
        void handleNewBlock(object? sender, BlockReplacementEventArgs e)
        {
            blockHash = e.Block.Hash;
            test.BlockTree.BlockAddedToMain -= handleNewBlock;
        }
        test.BlockTree.BlockAddedToMain += handleNewBlock;

        var newFilterResp = RpcTest.TestRequest(test.EthRpcModule, "eth_newFilter", "{\"fromBlock\":\"latest\"}");
        string getFilterLogsSerialized1 = test.TestEthRpc("eth_getFilterChanges", (newFilterResp as JsonRpcSuccessResponse)!.Result?.ToString() ?? "0x0");

        //expect empty - no changes so far
        Assert.That(getFilterLogsSerialized1, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"));

        await test.AddBlock(createCodeTx);

        //expect new transaction logs
        string getFilterLogsSerialized2 = test.TestEthRpc("eth_getFilterChanges", (newFilterResp as JsonRpcSuccessResponse)!.Result?.ToString() ?? "0x0");
        Assert.That(getFilterLogsSerialized2, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[{\"address\":\"0x0ffd3e46594919c04bcfd4e146203c8255670828\",\"blockHash\":\"0xf9fc52a47b7da4e8227cd60e9c368fa7d44df7f3226d5163005eec015588d64b\",\"blockNumber\":\"0x4\",\"data\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[],\"transactionHash\":\"0x8c9c109bff7969c8aed8e51ab4ea35c6f835a0c3266bc5c5721821a38cbf5445\",\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x0\"}],\"id\":67}"));

        //expect empty - previous call cleans logs
        string getFilterLogsSerialized3 = test.TestEthRpc("eth_getFilterChanges", (newFilterResp as JsonRpcSuccessResponse)!.Result?.ToString() ?? "0x0");
        Assert.That(getFilterLogsSerialized3, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_changes_with_tx()
    {
        using Context ctx = await Context.Create();
        _ = ctx.Test.TestEthRpc("eth_newPendingTransactionFilter");
        ctx.Test.AddTransactions(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).TestObject);
        string serialized2 = ctx.Test.TestEthRpc("eth_getFilterChanges", "0");

        Assert.That(serialized2, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[\"0x190d9a78dbc61b1856162ab909976a1b28ba4a41ee041341576ea69686cd3b29\"],\"id\":67}"), serialized2.Replace("\"", "\\\""));
    }

    [TestCase("earliest", "0x0000000000000000000000000000000000000000000000000000000000abcdef")]
    [TestCase("latest", "0x0000000000000000000000000000000000000000000000000000000000abcdef")]
    [TestCase("pending", "0x0000000000000000000000000000000000000000000000000000000000abcdef")]
    [TestCase("0x0", "0x0000000000000000000000000000000000000000000000000000000000abcdef")]
    public async Task Eth_get_storage_at(string blockParameter, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getStorageAt", TestItem.AddressA.Bytes.ToHexString(true), "0x1", blockParameter);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\",\"id\":67}}"));
    }

    [Test]
    public async Task Eth_get_storage_at_default_block()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getStorageAt", TestItem.AddressA.Bytes.ToHexString(true), "0x1");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0000000000000000000000000000000000000000000000000000000000abcdef\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_block_number()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_blockNumber");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x3\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_balance_internal_error()
    {
        using Context ctx = await Context.Create();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns((Block?)null);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockTree).Build();
        string serialized = ctx.Test.TestEthRpc("eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true), "0x01");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Incorrect head block\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_balance_incorrect_parameters()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getBalance", TestItem.KeccakA.Bytes.ToHexString(true), "0x01");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_syncing_true()
    {
        using Context ctx = await Context.Create();

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        for (int i = 0; i < 897; ++i)
        {
            await ctx.Test.AddBlock();
        }

        BlockHeader header = ctx.Test.BlockTree.Genesis!;
        for (int i = 0; i < 1000; i++)
        {
            BlockHeader newHeader = Build.A.BlockHeader.WithParent(header).TestObject;
            ctx.Test.BlockTree.Insert(newHeader);
            header = newHeader;
        }

        string serialized = ctx.Test.TestEthRpc("eth_syncing");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x384\",\"highestBlock\":\"0x3e8\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_syncing_false()
    {
        using Context ctx = await Context.Create();

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        for (int i = 0; i < 897; ++i)
        {
            await ctx.Test.AddBlock();
        }

        BlockHeader header = ctx.Test.BlockTree.Genesis!;
        for (int i = 0; i < 901; i++)
        {
            BlockHeader newHeader = Build.A.BlockHeader.WithParent(header).TestObject;
            ctx.Test.BlockTree.Insert(newHeader);
            header = newHeader;
        }

        string serialized = ctx.Test.TestEthRpc("eth_syncing");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":false,\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_logs()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.TryGetLogs(1, out Arg.Any<IEnumerable<FilterLog>>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                x[1] = new[] { new FilterLog(1, 0, 1, TestItem.KeccakA, 1, TestItem.KeccakB, TestItem.AddressA, new byte[] { 1, 2, 3 }, new[] { TestItem.KeccakC, TestItem.KeccakD }) };
                return true;
            });

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
        string serialized = ctx.Test.TestEthRpc("eth_getFilterLogs", "0x01");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"data\":\"0x010203\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"],\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"transactionIndex\":\"0x1\",\"transactionLogIndex\":\"0x0\"}],\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_logs_filter_not_found()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.TryGetLogs(5, out Arg.Any<IEnumerable<FilterLog>>(), Arg.Any<CancellationToken>())
                .Returns(x => { x[1] = null; return false; });

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
        string serialized = ctx.Test.TestEthRpc("eth_getFilterLogs", "0x05");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Filter with id: '5' does not exist.\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_logs_filterId_overflow()
    {
        using Context ctx = await Context.Create();

        UInt256 filterId = int.MaxValue + (UInt256)10;

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        string serialized = ctx.Test.TestEthRpc("eth_getFilterLogs", $"0x{filterId.ToString("X")}");

        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32603,\"message\":\"Filter with id: '{filterId}' does not exist.\"}},\"id\":67}}"));
    }

    [Test]
    public async Task Eth_get_logs_get_filter_logs_same_result()
    {
        byte[] logCreateCode = Prepare.EvmCode
                .PushData(32)
                .PushData(0)
                .Op(Instruction.LOG0)
                .Done;

        Transaction createCodeTx = Build.A.Transaction
            .SignedAndResolved(TestItem.PrivateKeyA).WithChainId(TestBlockchainIds.ChainId).WithGasPrice(2)
            .WithCode(logCreateCode)
            .WithNonce(3).WithGasLimit(210200).WithGasPrice(20.GWei()).TestObject;

        var test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(initialValues: 2.Ether());

        Keccak? blockHash = Keccak.Zero;
        void handleNewBlock(object? sender, BlockReplacementEventArgs e)
        {
            blockHash = e.Block.Hash;
            test.BlockTree.BlockAddedToMain -= handleNewBlock;
        }
        test.BlockTree.BlockAddedToMain += handleNewBlock;

        await test.AddBlock(createCodeTx);

        string getLogsSerialized = test.TestEthRpc("eth_getLogs", $"{{\"fromBlock\":\"{blockHash}\"}}");

        var newFilterResp = RpcTest.TestRequest(test.EthRpcModule, "eth_newFilter", $"{{\"fromBlock\":\"{blockHash}\"}}");

        Assert.IsTrue(newFilterResp is not null && newFilterResp is JsonRpcSuccessResponse);

        string getFilterLogsSerialized = test.TestEthRpc("eth_getFilterLogs", (newFilterResp as JsonRpcSuccessResponse)!.Result?.ToString() ?? "0x0");

        Assert.That(getFilterLogsSerialized, Is.EqualTo(getLogsSerialized));
    }

    [TestCase("{}", "{\"jsonrpc\":\"2.0\",\"result\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"data\":\"0x010203\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"],\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"transactionIndex\":\"0x1\",\"transactionLogIndex\":\"0x0\"}],\"id\":67}")]
    [TestCase("{\"fromBlock\":\"0x2\",\"toBlock\":\"latest\",\"address\":\"0x00000000000000000001\",\"topics\":[\"0x00000000000000000000000000000001\"]}", "{\"jsonrpc\":\"2.0\",\"result\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"data\":\"0x010203\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"],\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"transactionIndex\":\"0x1\",\"transactionLogIndex\":\"0x0\"}],\"id\":67}")]
    [TestCase("{\"fromBlock\":\"earliest\",\"toBlock\":\"pending\",\"address\":[\"0x00000000000000000001\", \"0x00000000000000000001\"],\"topics\":[\"0x00000000000000000000000000000001\", \"0x00000000000000000000000000000002\"]}", "{\"jsonrpc\":\"2.0\",\"result\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"data\":\"0x010203\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"],\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"transactionIndex\":\"0x1\",\"transactionLogIndex\":\"0x0\"}],\"id\":67}")]
    [TestCase("{\"topics\":[null, [\"0x00000000000000000000000000000001\", \"0x00000000000000000000000000000002\"]]}", "{\"jsonrpc\":\"2.0\",\"result\":[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"data\":\"0x010203\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"],\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"transactionIndex\":\"0x1\",\"transactionLogIndex\":\"0x0\"}],\"id\":67}")]
    [TestCase("{\"fromBlock\":\"0x10\",\"toBlock\":\"latest\",\"address\":\"0x00000000000000000001\",\"topics\":[\"0x00000000000000000000000000000001\"]}", "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"16 could not be found\"},\"id\":67}")]
    [TestCase("{\"fromBlock\":\"0x2\",\"toBlock\":\"0x11\",\"address\":\"0x00000000000000000001\",\"topics\":[\"0x00000000000000000000000000000001\"]}", "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"17 could not be found\"},\"id\":67}")]
    [TestCase("{\"fromBlock\":\"0x2\",\"toBlock\":\"0x1\",\"address\":\"0x00000000000000000001\",\"topics\":[\"0x00000000000000000000000000000001\"]}", "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"'From' block '2' is later than 'to' block '1'.\"},\"id\":67}")]
    public async Task Eth_get_logs(string parameter, string expected)
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetLogs(Arg.Any<BlockParameter>(), Arg.Any<BlockParameter>(), Arg.Any<object>(), Arg.Any<IEnumerable<object>>(), Arg.Any<CancellationToken>()).Returns(new[] { new FilterLog(1, 0, 1, TestItem.KeccakA, 1, TestItem.KeccakB, TestItem.AddressA, new byte[] { 1, 2, 3 }, new[] { TestItem.KeccakC, TestItem.KeccakD }) });
        bridge.FilterExists(1).Returns(true);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
        string serialized = ctx.Test.TestEthRpc("eth_getLogs", parameter);

        Assert.That(serialized, Is.EqualTo(expected));
    }

    [TestCase("{\"fromBlock\":\"earliest\",\"toBlock\":\"latest\"}", "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"resource not found message\"},\"id\":67}")]
    public async Task Eth_get_logs_with_resourceNotFound(string parameter, string expected)
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetLogs(Arg.Any<BlockParameter>(), Arg.Any<BlockParameter>(), Arg.Any<object>(), Arg.Any<IEnumerable<object>>(), Arg.Any<CancellationToken>())
            .Throws(new ResourceNotFoundException("resource not found message"));
        bridge.FilterExists(1).Returns(true);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
        string serialized = ctx.Test.TestEthRpc("eth_getLogs", parameter);

        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public async Task Eth_tx_count_by_hash()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getBlockTransactionCountByHash", ctx.Test.BlockTree.Genesis!.Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_uncle_count_by_hash()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getUncleCountByBlockHash", ctx.Test.BlockTree.Genesis!.Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}"));
    }

    [TestCase("earliest", "\"0x0\"")]
    [TestCase("latest", "\"0x0\"")]
    [TestCase("pending", "\"0x0\"")]
    [TestCase("0x0", "\"0x0\"")]
    public async Task Eth_uncle_count_by_number(string blockParameter, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getUncleCountByBlockNumber", blockParameter);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedResult},\"id\":67}}"));
    }

    [TestCase("earliest", "\"0x0\"")]
    [TestCase("latest", "\"0x2\"")]
    [TestCase("pending", "\"0x2\"")]
    [TestCase("0x0", "\"0x0\"")]
    public async Task Eth_tx_count_by_number(string blockParameter, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getBlockTransactionCountByNumber", blockParameter);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedResult},\"id\":67}}"));
    }

    [TestCase(false, false, "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, false, "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x16af125b31ba6f33725bffd77d8778121c8b24c3c29a9821d2fc15049a5bdcb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":\"0x0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"size\":\"0x21b\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":0,\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(false, true, "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x0\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, true, "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x16af125b31ba6f33725bffd77d8778121c8b24c3c29a9821d2fc15049a5bdcb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":\"0x0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"size\":\"0x21b\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":0,\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x0\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    public async Task Eth_get_block_by_hash(bool aura, bool eip1559, string expected)
    {
        using Context ctx = eip1559 ? await Context.CreateWithLondonEnabled() : await Context.Create();
        TestRpcBlockchain testBlockchain = (aura ? ctx.AuraTest : ctx.Test);
        string serialized = testBlockchain.TestEthRpc("eth_getBlockByHash", testBlockchain.BlockTree.Genesis!.Hash!.ToString(), "true");
        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public async Task Eth_get_block_by_hash_null()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getBlockByHash", Keccak.Zero.ToString(), "true");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}"));
    }

    [TestCase("0x71eac5e72c3b64431c246173352a8c625c8434d944eb5f3f58204fec3ec36b54", false, "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x49e7d7466be0927347ff2f654c014a768b5a5fcd8c483635210466dd0d6d204c\",\"receiptsRoot\":\"0xd95b673818fa493deec414e01e610d97ee287c9421c8eff4102b1647c1a184e4\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x4e786afc8bed76b7299973ca70022b367cbb94c14ec30e9e7273b31b6b968de9\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\"],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
    [TestCase("0x71eac5e72c3b64431c246173352a8c625c8434d944eb5f3f58204fec3ec36b54", true, "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x49e7d7466be0927347ff2f654c014a768b5a5fcd8c483635210466dd0d6d204c\",\"receiptsRoot\":\"0xd95b673818fa493deec414e01e610d97ee287c9421c8eff4102b1647c1a184e4\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x4e786afc8bed76b7299973ca70022b367cbb94c14ec30e9e7273b31b6b968de9\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[{\"hash\":\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"nonce\":\"0x1\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x0\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"}],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
    public async Task Eth_get_block_by_hash_with_tx(string blockParameter, bool withTxData, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getBlockByHash", ctx.Test.BlockTree.Head!.Hash!.ToString(), withTxData.ToString());
        Assert.That(serialized, Is.EqualTo(expectedResult), serialized.Replace("\"", "\\\""));
    }

    [TestCase(false, "earliest", "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(false, "latest", "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x49e7d7466be0927347ff2f654c014a768b5a5fcd8c483635210466dd0d6d204c\",\"receiptsRoot\":\"0xd95b673818fa493deec414e01e610d97ee287c9421c8eff4102b1647c1a184e4\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x4e786afc8bed76b7299973ca70022b367cbb94c14ec30e9e7273b31b6b968de9\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[{\"hash\":\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"nonce\":\"0x1\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x0\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"}],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
    [TestCase(false, "pending", "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x49e7d7466be0927347ff2f654c014a768b5a5fcd8c483635210466dd0d6d204c\",\"receiptsRoot\":\"0xd95b673818fa493deec414e01e610d97ee287c9421c8eff4102b1647c1a184e4\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x4e786afc8bed76b7299973ca70022b367cbb94c14ec30e9e7273b31b6b968de9\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[{\"hash\":\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"nonce\":\"0x1\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x0\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"}],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
    [TestCase(false, "0x0", "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, "earliest", "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x0\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, "latest", "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x7a1200\",\"gasUsed\":\"0x0\",\"hash\":\"0x16b111d85efa64c1c8e27f1e59c8ccd6bb6643b1999628ac37294c31158e2245\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x761cfe357802c8a2a68e37ad8325607920e72ce19b5b0d3e1ba01840f7e905ec\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x20b\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"baseFeePerGas\":\"0x2da282a8\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, "0x0", "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x0\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(false, "0x20", "{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}")]
    public async Task Eth_get_block_by_number(bool eip1559, string blockParameter, string expectedResult)
    {
        using Context ctx = eip1559 ? await Context.CreateWithLondonEnabled() : await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getBlockByNumber", blockParameter, "true");
        Assert.That(serialized, Is.EqualTo(expectedResult), serialized.Replace("\"", "\\\""));
    }

    [TestCase("earliest", "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase("latest", "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x49e7d7466be0927347ff2f654c014a768b5a5fcd8c483635210466dd0d6d204c\",\"receiptsRoot\":\"0xd95b673818fa493deec414e01e610d97ee287c9421c8eff4102b1647c1a184e4\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x4e786afc8bed76b7299973ca70022b367cbb94c14ec30e9e7273b31b6b968de9\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\"],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
    [TestCase("pending", "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x49e7d7466be0927347ff2f654c014a768b5a5fcd8c483635210466dd0d6d204c\",\"receiptsRoot\":\"0xd95b673818fa493deec414e01e610d97ee287c9421c8eff4102b1647c1a184e4\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x4e786afc8bed76b7299973ca70022b367cbb94c14ec30e9e7273b31b6b968de9\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\"],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
    [TestCase("0x0", "{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase("0x20", "{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}")]
    public async Task Eth_get_block_by_number_no_details(string blockParameter, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getBlockByNumber", blockParameter, "false");
        Assert.That(serialized, Is.EqualTo(expectedResult), serialized.Replace("\"", "\\\""));

        string serialized2 = ctx.Test.TestEthRpc("eth_getBlockByNumber", blockParameter);
        Assert.That(serialized2, Is.EqualTo(expectedResult), serialized2);
    }

    [TestCase("0x0")]
    public async Task Eth_get_block_by_number_should_not_recover_tx_senders_for_request_without_tx_details(string blockParameter)
    {
        IBlockchainBridge? blockchainBridge = Substitute.For<IBlockchainBridge>();
        TestRpcBlockchain ctx = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(blockchainBridge).Build(MainnetSpecProvider.Instance);
        ctx.TestEthRpc("eth_getBlockByNumber", blockParameter, "false");
        blockchainBridge.Received(0).RecoverTxSenders(Arg.Any<Block>());
    }


    [Test]
    public async Task Eth_get_block_by_number_null()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getBlockByNumber", "1000000", "false");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}"));
    }

    [Test]
    public async Task Eth_protocol_version()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_protocolVersion");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x42\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_code()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getCode", TestItem.AddressA.ToString(), "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0xabcd\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_code_default()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getCode", TestItem.AddressA.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0xabcd\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_mining_true()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.IsMining.Returns(true);
        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();

        string serialized = ctx.Test.TestEthRpc("eth_mining");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}"));
    }

    [Test]
    public async Task Eth_mining_false()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.IsMining.Returns(false);
        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();

        string serialized = ctx.Test.TestEthRpc("eth_mining");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":false,\"id\":67}"));
    }

    [Test]
    public async Task Eth_accounts()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_accounts");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[\"0x7e5f4552091a69125d5dfcb7b8c2659029395bdf\",\"0x2b5ad5c4795c026514f8317c7a215e218dccd6cf\",\"0x6813eb9362372eef6200f3b1dbc3f819671cba69\",\"0x1eff47bc3a10a45d4b230b5d10e37751fe6aa718\",\"0xe1ab8145f7e55dc933d51a18c793f901a3a0b276\",\"0xe57bfe9f44b819898f47bf37e5af72a0783e1141\",\"0xd41c057fd1c78805aac12b0a94a405c0461a6fbb\",\"0xf1f6619b38a98d6de0800f1defc0a6399eb6d30c\",\"0xf7edc8fa1ecc32967f827c9043fcae6ba73afa5c\",\"0x4cceba2d7d2b4fdce4304d3e09a1fea9fbeb1528\",\"0x3da8d322cb2435da26e9c9fee670f9fb7fe74e49\",\"0xdbc23ae43a150ff8884b02cea117b22d1c3b9796\",\"0x68e527780872cda0216ba0d8fbd58b67a5d5e351\",\"0x5a83529ff76ac5723a87008c4d9b436ad4ca7d28\",\"0x8735015837bd10e05d9cf5ea43a2486bf4be156f\",\"0xfae394561e33e242c551d15d4625309ea4c0b97f\",\"0x252dae0a4b9d9b80f504f6418acd2d364c0c59cd\",\"0x79196b90d1e952c5a43d4847caa08d50b967c34a\",\"0x4bd1280852cadb002734647305afc1db7ddd6acb\",\"0x811da72aca31e56f770fc33df0e45fd08720e157\",\"0x157bfbecd023fd6384dad2bded5dad7e27bf92e4\",\"0x37da28c050e3c0a1c0ac3be97913ec038783da4c\",\"0x3bc8287f1d872df4217283b7920d363f13cf39d8\",\"0xf4e2b0fcbd0dc4b326d8a52b718a7bb43bdbd072\",\"0x9a5279029e9a2d6e787c5a09cb068ab3d45e209d\",\"0xc39677f5f47d5fe65ab24e66750e8fca127c15be\",\"0x1dc728786e09f862e39be1f39dd218ee37feb68d\",\"0x636cc65783084b9f370789c90f733dbbeb88925d\",\"0x4a7a7c2e09209dbe44a582cd92b0edd7129e74be\",\"0xa56160a359f2eaa66f5c9df5245542b07339a9a6\",\"0x6b09d6433a379752157fd1a9e537c5cae5fa3168\",\"0x32e77de0d74a5c7af861aaed324c6a4c488142a8\",\"0x093d49d617a10f26915553255ec3fee532d2c12f\",\"0x138854708d8b603c9b7d4d6e55b6d32d40557f4d\",\"0x7dc0a40d64d72bb4590652b8f5c687bf7f26400c\",\"0x9358a525cc25aa571af0bcb5b98fbeab045a5e36\",\"0xd8e8ea89d71de89214fa39ba13ba9fcddc0d9467\",\"0xb56ed8f48979e1a948ad129199a600d0562cac51\",\"0xf65ac7003e905d72c666bfec1dc0960ecc9d0d6e\",\"0xd817d23c981472d703be36da777ffdb1abefd972\",\"0xf2adb90aa27a3c61a95c50063b20919d811e1476\",\"0xae3dffee97f92db0201d11cb8877c89738353bce\",\"0xeb3025e7ac2764040384316b33476e048961a71f\",\"0x9e3289708dc5709926a542fcf260fd4b210461f0\",\"0x6c23face014f20b3ebb65ae96d0d7ff32ab94c17\",\"0xb83b6241f966b1685c8b2ffce3956e21f35b4dcb\",\"0x6350872d7465864689def650443026f2f73a08da\",\"0x673c638147fe91e4277646d86d5ae82f775eea5c\",\"0xf472086186382fca55cd182de196520abd76f69d\",\"0x5ae58d2bc5145bff0c1bec0f32bfc2d079bc66ed\",\"0x2b29bea668b044b2b355c370f85b729bcb43ec40\",\"0x3797126345fb5fb6a37629db55ec692173cfb458\",\"0xe6869cc98283ab53e8a1a5312857ef0be9d189fe\",\"0xa5dfe354b3fc30c5c3a8ffefc8f9470d9177c334\",\"0xa1a625ae13b80a9c48b7c0331c83bc4541ac137f\",\"0xa33c9d26e1e33b84247defca631c1d30ffc76f5d\",\"0xf9807a8719ae154e74591cb2d452797707fadf73\",\"0xa1ba6fc3ea0e89f0e79f89d9aa0081d010571e4a\",\"0x366c20b40048556e5682e360997537c3715aca0e\",\"0xeb0e56f32246d043228fac8b63a71687d5199af1\",\"0xdb3ed822b78f0641623a12166607b5fa4df862ad\",\"0xb88c19426f03c6981d1a4281c7414d842b97619a\",\"0x32e04b012ac811c91d36a355a6d2859a0071a965\",\"0xe0dd44773f7657b11019062879d65f3d9862460c\",\"0x756be12856a8f44ab22fdbcbd42b70b843377d09\",\"0x6f4c950442e1af093bcff730381e63ae9171b87a\",\"0x4d1bf28514a4451249908e611366ec967c3d1558\",\"0xb0142d883494197b02c6ece84f571d81bd831124\",\"0x1326324f5a9fb193409e10006e4ea41b970df321\",\"0xf9a2c330a19e2fbfeb50fe7a7195b973bb0a3be9\",\"0x7a601ffa997cede6435aeabf4fa2091f09e149ec\",\"0xa92f4b5c4fddcc37e5139873ac28a4a0a42d68df\",\"0x850cc185d6cae4a7fdfb3dd81f977dd1df7d6503\",\"0xb1b7c87e8a0bf2e7fd1a1c582bd353e4c4529341\",\"0xff844fdb49e00776ad538db9ea2f9fa98ec0caf7\",\"0x1ac6f9601f2f616badcea8a0a307e1a3c14767a4\",\"0xc2aa6271409c10dee630e79df90c968989ccf2b7\",\"0x883d01eae6eaac077e126ddb32cd53550966ed76\",\"0x127688bbc070dd69a4db8c3ba5d43909e13d8f77\",\"0x0b54a50c0409dab2e63c3566324268ed53ec019a\",\"0xafd46e3549cc63d7a240d6177d056857679e6f99\",\"0x752481f35bb1d44d786c7b4dbe40db4a4266f96f\",\"0xac32def421e36b43629f785fd04523260e7f2b28\",\"0xfe6032a0810e90d025a3a39dd29844f964ee102c\",\"0x5cb6f3e6499d1f068b33351d0cae4b68cdf501bf\",\"0x84b743441b7bdf65cb4293126db4c1b709d7d95e\",\"0x8530a26f6c062f55597bd30c1a44e248decb0027\",\"0x5ce162cfa6208d7c50a7cb3525ac126155e7bce4\",\"0x2853dc9ca40d012969e25360cce0d9d326b24a86\",\"0x802271c02f76701929e1ea772e72783d28e4b60f\",\"0x7bd2aa0726ac3b9e752b120de8568e90b0423ae4\",\"0xb540c05d9b2516da9596a5ee75d750717a4be035\",\"0xa72392cd4be285ab6681da1bf1c45f0b370cb7b4\",\"0xcf484269182ac2388a4bfe6d19fb0687e3534b7f\",\"0x994907cb80bfd175f9b0b32672cfde0091368e2e\",\"0x36eab6ce7fededc098ef98c41e83548a89147131\",\"0x440db3ab842910d2a39f4e1be9e017c6823fb658\",\"0x25ac70ea6f44c4531a7117ea3620fa29cdaaca48\",\"0x24d881139ee639c2a774b4b1851cb7a9d0fce122\",\"0xd9a284367b6d3e25a91c91b5a430af2593886eb9\",\"0xe6b3367318c5e11a6eed3cd0d850ec06a02e9b90\",\"0x88c0e901bd1fd1a77bda342f0d2210fdc71cef6b\",\"0x7231c364597f3bfdb72cf52b197cc59111e71794\",\"0x043aed06383f290ee28fa02794ec7215ca099683\",\"0x0c95931d95694b3ef74071241827c09f25d40620\",\"0x417f3b59ef57c641283c2300fae0f27fe98d518c\",\"0xd6b931d8d441b1ec98f55f8ec8adb532dc140c78\",\"0x9220625b1a30680387d542e6b5f753786ca5530e\",\"0x997cf669860a1dcc76344866534d8679a7b562e2\",\"0xb961768b578514debf079017ff78c47b0a6adbf6\",\"0x052b91ad9732d1bce0ddae15a4545e5c65d02443\",\"0x8df64de79608f0ae9e72ecae3a400582aed8101c\",\"0x0e7b23cd1fdb7ea3ccc80320ab43843a2f193c36\",\"0xfbbc41289f834a76e4320ac238512035560467ee\",\"0x61e1da6c7b8b211e6e5dd921efe27e73ad226dac\",\"0x87fcbe64187317c59a944be5b9c5c830b9373730\",\"0x2acf0d6fdac920081a446868b2f09a8dac797448\",\"0x1715eb68afba4d516ef1e068b55f5093bb4a2f59\",\"0x58bab2f728dc4fc227a4c38cab2ec93b73b4e828\",\"0x25346934b4faa00dee0190c2069156bde6010c18\",\"0xa01cca6367a84304b6607b76676c66c360b74741\",\"0x872917cec8992487651ee633dba73bd3a9dca309\",\"0x6c1a01c2ab554930a937b0a2e8105fb47946c679\",\"0x13c0e7c715fdea35c7f9663c719e4d36601275b9\",\"0xe8c5025c803f3279d94ea55598e147f601929bf4\",\"0x639acdbd838b81cea8d6a970136812783fa5bf5e\",\"0xb3087f34edab33a8182ba29adea4d739d9831a94\",\"0xc6a210606f2ee6e64afb9584db054f3476a5cc66\",\"0xd01c9d93efc83c00b30f768f832182beff65696f\",\"0x00edf2d16afbc028fb1e879559b07997af79539f\",\"0xf5d722030d17ca01f2813af9e7be158d7a037997\",\"0xae3d43ab6fdcd35386db427099ff11aa670ee0f4\",\"0x0dc8b8ef8457b1e45ac277d65ac5987b547ba775\",\"0xde521346f9327a4314a18b2cda96b2a33603177a\",\"0x69842e12d6f36f9f93f06086b70795bfc7e02745\",\"0x9b7bdf6ad17d5fc9a168acaa24495e52a65f3b79\",\"0xa2d47d2c42009520075cb15f5855052008d0c44d\",\"0xb0c249f6f92fb2491fc9750a5299d856ba2ea3c6\",\"0x839d96957f21e82fbcca0d42a1f12ef6e1cf82e9\",\"0x2a0d6b92b042497013e5549d6579202608ce0c80\",\"0xa4f8c598927eab2f1898f8f2d6f8121578de2344\",\"0xdb21655b672dacc8da6f538c899f9d6969604117\",\"0x21289cd01f9f58fc44962b6e213a0fbbd015beb6\",\"0x0b62d63c314d94dfa85b11a9c652ffe438382d6c\",\"0x9383e3096133f464d516b518b12851fd10d891f4\",\"0x64e582c17ab7c3b90e171795b504ca3c04108501\",\"0x848406919d014b1e5c27a82f951caff840fd63ef\",\"0x5fe015779fb36006b01f9c5a5dbcaa6ffa56f0c0\",\"0x28b6e15f86025b8ea8beaa6855a81069bfb6ab1e\",\"0x271d65af9a5a7b4cd7af264f251184c2a4b9e7a3\",\"0xddf44e34ed40c40624c7b9f20a1030b505a4fac0\",\"0xe5854075272ca5ef71663d5b87e0cd5ac53b2f36\",\"0x2798ba84d7830c5f60d750f37f87d93277106905\",\"0x7e9961fa09dd52f945f8143844785cf0e51bb4ce\",\"0xf33d2f7d96f92d912ca8418f9d62eb54c1a9889f\",\"0xeec566c793a89f388bbabfc0225183a6a95c4263\",\"0x2001f8cdcdeef1bbcc188ca59cf04fb44133d55a\",\"0x3bf958fa0626e898f548a8f95cf9ab3a4db65169\",\"0xb0d744fde06bbcb6655eb55288ec94fa6a0b2a52\",\"0x18eb36d090eeadf82f3454a6da690fc398d3eba1\",\"0xd2431ca38735c2fd438e2caa23f094191d89675b\",\"0x612b7be154a64292aae070aaa86fcd66ba218071\",\"0x681ce2f439fdc80e70c1eea8b8a085dfb976d32a\",\"0x2174ca3ee9ace7dd8c946c97054c72f2b384c4c2\",\"0x1d694d5ad94f32132ff5c14c901d3ddbee90a550\",\"0x0b6fe046e6fd8d7a7a36d5ad1ffb82d2e3e5c3bb\",\"0x258f4ed0560e290a95066d9dee3628f2f179302b\",\"0xe2a09565167d4e3f826adec6bef82b97e0a4383f\",\"0x9af70704e9ec5f505cdba564ff4dec03503ddaa2\",\"0xeb9afe072c781401bf364224c75a036e4d832f52\",\"0x07748403082b29a45abd6c124a37e6b14e6b1803\",\"0x63486b70d804464766cfd096bba5552c4bcdac30\",\"0x5181be40152caaba8e123a55b7762755d4e8e416\",\"0x9481da7766c043eefeecc9589ee7ade61316b0ff\",\"0x42aba3530dd1ccb1dda27bfaa7c6a832cfdb4446\",\"0x05650444ace15a01762bd97ee8fdeb495b3c2436\",\"0xd83d18a2eae2440e272a53f86e617cd9f33c8d68\",\"0x4a35a802dbd623561040dd50f6293842d0901731\",\"0x4dbaf6c348d8cd1f174a7a6155f80ea8d4a8baf8\",\"0x9efc4e49be8ff70d596ac20efec9b7842e1ea963\",\"0x68efde0cdd917c6da6dab02c23f69e7c9cff51a9\",\"0x99b52813933a46d95bd4265ea2f674e58827da97\",\"0x7b35461cc5adbdc415c1f9562ccc342adbf09bd4\",\"0x8ee8813fb9d41cc58ef87d28b36e948b1234e71d\",\"0x69c1bc7883a7bb7696c7726d025867cd16564c9c\",\"0x31eb18dd6f5a8064ab750eabb281cf162f43ccd0\",\"0xf5d122e123d9d7998d2bea685d11b10fec3e4508\",\"0xf762854586a40a93d1fdcde32c062829f3754de9\",\"0x1e3f8fb9f840325983d6e5c68b6b846ff66a20ac\",\"0x3c1638a25ad7e8c2a84b53b661dd1bd048407e8f\",\"0x2eedf8a799b73bc02e4664183eb72422c377153b\",\"0xcdef6f23a26f960b53468f497967ce74c026af52\",\"0x0a2035683fe5587b0b09db0e757306ad729fe6c1\",\"0x158cc083cfbcffb2f983a3aa8b027eb0711c9831\",\"0x691cb1645a4f21d879973b3a3b98a714fc1970d6\",\"0x754164c0cb85dda1b5b18e5b62adbb4d60c3efbf\",\"0x556330e8d92912ccf133851ba03abd2db70da404\",\"0x1745ceba112b0a41638e235ec59b35adf37b70ab\",\"0xa24c85b16a440587793f82e358fa6b204468735b\",\"0x5304fb08724d73f2bb5e04c582407c33cde6c8d3\",\"0x256a11785fc43141324cf61efb5f491378c10c85\",\"0xa9f161a2badd44f3fe45b91a044a9484b72f1dc4\",\"0xd5cc10c45fc0f9f956acd7559f61edbfec9f6c3d\",\"0x381c7a71035bdb42fb5d77523df2ff00d9f9df1b\",\"0x45cdbeea730d8212f451a6a8d0eb5998b04cccca\",\"0x6367283f25a32be0c28623d787c319e237c3b7bd\",\"0x598e94eb5e050045272d8417f6ab363bd874d568\",\"0x379ff6375f4a44f458683629ef05141d73fae55a\",\"0x18df8ba2ef19083ddff68f8b33976cf22e8419c6\",\"0xffceebc37a7351d5df9aa3b077ed39cc3b5192ff\",\"0x1cd21f00b58894260f7abed65ad23dce3cea0226\",\"0x26324733d604abb6176cf18e4f4a0624ceeddc09\",\"0x4102d394d723ff141b82ef9a6053fb89f90c67f4\",\"0x269c370cb95b63f9b6a7cad47998167f160a2689\",\"0x3bb9557113fbb052dae3008a2801a072c432c018\",\"0x3f588a72d94d0d0986b112c671c2343320a19386\",\"0x7cfa9eee1d752da599211bc8a68d0687708dabfc\",\"0x7bc23966c419eadcb8a2fc5f83e635c4d4ad0c2f\",\"0xc4ad60337b04fc721912531a52a5d77878293fb9\",\"0xfc5ba3041f750f9b6820ce066c153eb396aac1ff\",\"0x32480c2d857941d2fff4e34f0910b20c0f9c23bc\",\"0x8041c9a96585053db2d7214b5de56828645b8e62\",\"0x444ca66b3ceb4187840cb1a205566a1413d5fecb\",\"0xf084bbaabee1a700a8faa404027db620a5aa0059\",\"0x602d562b4ef2544f851587619b56f77a9d965d45\",\"0x216faec139a61329ef8b31d982de427d9c007a9e\",\"0x11eb17b20113ae923d72e52870d40bf59a08b40d\",\"0xe69017fcc36bbc7fb167b9585bdd47a950ba1992\",\"0xe5549f429a72bfa618cf5c1afdac22a730df6a1a\",\"0x161c2e10407e2a87959c0bae1f342c80eaea28f3\",\"0x4161220db043a7d682e0ad123a3f8fea165711aa\",\"0xb33609811fb3d9fc8955dc6e9e086f1f08fc3a65\",\"0x4148555ea4c00e14f81ef399bbe67ef2fd9811b1\",\"0x4f81e991f76276a17ca92a1321f37189b1727f77\",\"0xba95e317ead06b55c8b70276fc63904b3339dfa1\",\"0xf6203c4fb14da640d11fbd9573e3958d017e6745\",\"0x73377d6228266393747efa710017872d6dd5b9a6\",\"0xf7862d105fc6ee69604decc30aa89472ad405961\",\"0xfa1205e19719c248323563bd55cd8bfd08b0cbc6\",\"0x4f46630115b446f8f7cebe1e5961ef7858c25204\",\"0x7492ebbc1e7f2838fc7191edc031581d5712978a\",\"0xc0af3981f9c0dfcb8955fea07a3e4f23806fab51\",\"0x8621dd642245df371b584b48c081e8863313a70d\",\"0xc328de035c91b39efa07d2fe620813253c9b4ec2\",\"0xa11308e3b741227d41973ddb17534ceb27b8206f\",\"0xc4ff1b4565ee203fa12636e100fe9c89cd18acb7\",\"0x63a36aea8570219476ef835f09024acdedfee95a\",\"0xf7205066c153f7c88dc3653ebc72b438884ae109\",\"0xa8ce5c40c4aa9278ddeaa418e775985549960e7a\",\"0x81f58f2194b0413806bf2ce8e1654bc855dc65a1\",\"0xf0218008120201e66b65fce4df9035007e811228\",\"0x90f022e3ca8453f5e5765cd3054003b544526eec\",\"0x1d1f873ba1ddf7915e6e26f93f5624b40efaefe2\",\"0x0311afd3bc2ae250d5f9f2706bae2ef4164d6912\",\"0x5044a80bd3eff58302e638018534bbda8896c48a\"],\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_block_by_number_with_number_bad_number()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getBlockByNumber", "'0x1234567890123456789012345678901234567890123456789012345678901234567890'", "true");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_proof()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getProof", TestBlockchain.AccountA.ToString(), "[]", "0x2");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"accountProof\":[\"0xf8718080808080a0fc8311b2cabe1a1b33ea04f1865132a44aa0c17c567acd233422f9cfb516877480808080a0be8ea164b2fb1567e2505295dae6d8a9fe5f09e9c5ac854a7da23b2bc5f8523ca053692ab7cdc9bb02a28b1f45afe7be86cb27041ea98586e6ff05d98c9b0667138080808080\",\"0xf8518080808080a00dd1727b2abb59c0a6ac75c01176a9d1a276b0049d5fe32da3e1551096549e258080808080808080a038ca33d3070331da1ccf804819da57fcfc83358cadbef1d8bde89e1a346de5098080\",\"0xf872a020227dead52ea912e013e7641ccd6b3b174498e55066b0c174a09c8c3cc4bf5eb84ff84d01893635c9adc5de9fadf7a0475ae75f323761db271e75cbdae41aede237e48bc04127fb6611f0f33298f72ba0dbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322\"],\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"balance\":\"0x3635c9adc5de9fadf7\",\"codeHash\":\"0xdbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322\",\"nonce\":\"0x1\",\"storageHash\":\"0x475ae75f323761db271e75cbdae41aede237e48bc04127fb6611f0f33298f72b\",\"storageProof\":[]},\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_proof_withTrimmedStorageKey()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getProof", TestBlockchain.AccountA.ToString(), "[\"0x1\"]", "0x2");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"accountProof\":[\"0xf8718080808080a0fc8311b2cabe1a1b33ea04f1865132a44aa0c17c567acd233422f9cfb516877480808080a0be8ea164b2fb1567e2505295dae6d8a9fe5f09e9c5ac854a7da23b2bc5f8523ca053692ab7cdc9bb02a28b1f45afe7be86cb27041ea98586e6ff05d98c9b0667138080808080\",\"0xf8518080808080a00dd1727b2abb59c0a6ac75c01176a9d1a276b0049d5fe32da3e1551096549e258080808080808080a038ca33d3070331da1ccf804819da57fcfc83358cadbef1d8bde89e1a346de5098080\",\"0xf872a020227dead52ea912e013e7641ccd6b3b174498e55066b0c174a09c8c3cc4bf5eb84ff84d01893635c9adc5de9fadf7a0475ae75f323761db271e75cbdae41aede237e48bc04127fb6611f0f33298f72ba0dbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322\"],\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"balance\":\"0x3635c9adc5de9fadf7\",\"codeHash\":\"0xdbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322\",\"nonce\":\"0x1\",\"storageHash\":\"0x475ae75f323761db271e75cbdae41aede237e48bc04127fb6611f0f33298f72b\",\"storageProof\":[{\"key\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"proof\":[\"0xe7a120b10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf68483abcdef\"],\"value\":\"0xabcdef\"}]},\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_block_by_number_empty_param()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getBlockByNumber", "", "true");
        Assert.True(serialized.StartsWith("{\"jsonrpc\":\"2.0\",\"error\""));
    }

    [Test]
    public async Task Eth_get_account_notfound()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getAccount", "0x000000000000000000000000000000000000dead", "latest");

        serialized.Should().Be("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}");
    }

    [Test]
    public async Task Eth_get_account_found()
    {
        using Context ctx = await Context.Create();
        string account_address = TestBlockchain.AccountC.ToString();

        string serialized = ctx.Test.TestEthRpc("eth_getAccount", account_address, "latest");
        string expected = "{\"jsonrpc\":\"2.0\",\"result\":{\"codeHash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"storageRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"balance\":\"0x3635c9adc5dea00000\",\"nonce\":\"0x0\"},\"id\":67}";

        serialized.Should().Be(expected);
    }

    [Test]
    public async Task Eth_get_account_incorrect_block()
    {
        using Context ctx = await Context.Create();
        string account_address = TestBlockchain.AccountC.ToString();

        string serialized = ctx.Test.TestEthRpc("eth_getAccount", account_address, "0xffff");

        serialized.Should().Be("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"65535 could not be found\"},\"id\":67}");
    }

    [Test]
    public async Task Eth_get_account_no_block_argument()
    {
        using Context ctx = await Context.Create();
        string account_address = TestBlockchain.AccountC.ToString();

        string serialized = ctx.Test.TestEthRpc("eth_getAccount", account_address);
        string expected = "{\"jsonrpc\":\"2.0\",\"result\":{\"codeHash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"storageRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"balance\":\"0x3635c9adc5dea00000\",\"nonce\":\"0x0\"},\"id\":67}";

        serialized.Should().Be(expected);
    }


    [Test]
    public async Task Eth_get_block_by_number_with_recovering_sender_from_receipts()
    {
        using Context ctx = await Context.Create();
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();

        Block block = Build.A.Block.WithNumber(1)
            .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
            .WithTransactions(new[] { Build.A.Transaction.TestObject })
            .TestObject;

        LogEntry[] entries = new[]
        {
            Build.A.LogEntry.TestObject,
            Build.A.LogEntry.TestObject
        };

        TxReceipt receipt = Build.A.Receipt.WithBloom(new Bloom(entries, new Bloom())).WithAllFieldsFilled
            .WithSender(TestItem.AddressE)
            .WithLogs(entries).TestObject;
        TxReceipt[] receiptsTab = { receipt };
        blockFinder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
        receiptFinder.Get(Arg.Any<Block>()).Returns(receiptsTab);
        receiptFinder.Get(Arg.Any<Keccak>()).Returns(receiptsTab);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockFinder).WithReceiptFinder(receiptFinder).Build();
        string serialized = ctx.Test.TestEthRpc("eth_getBlockByNumber", TestItem.KeccakA.ToString(), "true");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0xe3026a6708b90d5cb25557ac38ddc3f5ef550af10f31e1cf771524da8553fa1c\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x1\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x221\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0x0\",\"timestamp\":\"0xf4240\",\"transactions\":[{\"nonce\":\"0x0\",\"blockHash\":\"0xe3026a6708b90d5cb25557ac38ddc3f5ef550af10f31e1cf771524da8553fa1c\",\"blockNumber\":\"0x1\",\"transactionIndex\":\"0x0\",\"from\":\"0x2d36e6c27c34ea22620e7b7c45de774599406cf3\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"data\":\"0x\",\"input\":\"0x\",\"type\":\"0x0\"}],\"transactionsRoot\":\"0x29cc403075ed3d1d6af940d577125cc378ee5a26f7746cbaf87f1cf4a38258b5\",\"uncles\":[]},\"id\":67}"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Eth_get_transaction_receipt(bool postEip4844)
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();

        Block block = Build.A.Block.WithNumber(1)
            .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
            .TestObject;

        LogEntry[] entries = new[]
        {
            Build.A.LogEntry.TestObject,
            Build.A.LogEntry.TestObject
        };

        TxReceipt receipt = Build.A.Receipt.WithBloom(new Bloom(entries, new Bloom())).WithAllFieldsFilled
            .WithLogs(entries).TestObject;
        TxReceipt[] receiptsTab = { receipt };


        blockchainBridge.GetReceiptAndGasInfo(Arg.Any<Keccak>())
            .Returns((receipt, postEip4844 ? new(UInt256.One, 2, 3) : new(UInt256.One), 0));
        blockFinder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
        receiptFinder.Get(Arg.Any<Block>()).Returns(receiptsTab);
        receiptFinder.Get(Arg.Any<Keccak>()).Returns(receiptsTab);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockFinder).WithReceiptFinder(receiptFinder).WithBlockchainBridge(blockchainBridge).Build();
        string serialized = ctx.Test.TestEthRpc("eth_getTransactionReceipt", TestItem.KeccakA.ToString());

        if (postEip4844)
            Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"cumulativeGasUsed\":\"0x3e8\",\"gasUsed\":\"0x64\",\"dataGasUsed\":\"0x3\",\"dataGasPrice\":\"0x2\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x0\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x1\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"root\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"status\":\"0x1\",\"error\":\"error\",\"type\":\"0x0\"},\"id\":67}"));
        else
            Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"cumulativeGasUsed\":\"0x3e8\",\"gasUsed\":\"0x64\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x0\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x1\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"root\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"status\":\"0x1\",\"error\":\"error\",\"type\":\"0x0\"},\"id\":67}"));
    }


    [Test]
    public async Task Eth_get_transaction_receipt_when_block_has_few_receipts()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();

        int blockNumber = 1;
        Block genesis = Build.A.Block.Genesis
            .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
            .TestObject;
        Block previousBlock = genesis;
        Block block = Build.A.Block.WithNumber(blockNumber).WithParent(previousBlock)
            .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
            .TestObject;

        LogEntry[] logEntries = new[] { Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject };

        TxReceipt receipt1 = new TxReceipt()
        {
            Bloom = new Bloom(logEntries),
            Index = 1,
            Recipient = TestItem.AddressA,
            Sender = TestItem.AddressB,
            BlockHash = TestItem.KeccakA,
            BlockNumber = blockNumber,
            ContractAddress = TestItem.AddressC,
            GasUsed = 1000,
            TxHash = TestItem.KeccakA,
            StatusCode = 0,
            GasUsedTotal = 2000,
            Logs = logEntries
        };

        TxReceipt receipt2 = new TxReceipt()
        {
            Bloom = new Bloom(logEntries),
            Index = 2,
            Recipient = TestItem.AddressC,
            Sender = TestItem.AddressD,
            BlockHash = TestItem.KeccakA,
            BlockNumber = blockNumber,
            ContractAddress = TestItem.AddressC,
            GasUsed = 1000,
            TxHash = TestItem.KeccakB,
            StatusCode = 0,
            GasUsedTotal = 2000,
            Logs = logEntries
        };

        blockchainBridge.GetReceiptAndGasInfo(Arg.Any<Keccak>()).Returns((receipt2, new(UInt256.One), 2));

        TxReceipt[] receipts = { receipt1, receipt2 };

        blockFinder.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(blockNumber).TestObject);
        blockFinder.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(blockNumber).TestObject).TestObject);
        blockFinder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
        receiptFinder.Get(Arg.Any<Block>()).Returns(receipts);
        receiptFinder.Get(Arg.Any<Keccak>()).Returns(receipts);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockFinder).WithBlockchainBridge(blockchainBridge).WithReceiptFinder(receiptFinder).Build();
        string serialized = ctx.Test.TestEthRpc("eth_getTransactionReceipt", TestItem.KeccakA.ToString());

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x2\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x3\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_transaction_receipt_returns_null_on_missing_receipt()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_getTransactionReceipt", TestItem.KeccakA.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}"));
    }


    [Test]
    public async Task Eth_getTransactionReceipt_return_info_about_mined_tx()
    {
        using Context ctx = await Context.Create();
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();

        await ctx.Test.AddFunds(new Address("0x723847c97bc651c7e8c013dbbe65a70712f02ad3"), 1.Ether());
        Transaction tx = Build.A.Transaction.WithData(new byte[] { 0, 1 })
            .SignedAndResolved().WithChainId(TestBlockchainIds.ChainId).WithGasPrice(0).WithValue(0).WithGasLimit(210200).WithGasPrice(20.GWei()).TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
            .WithTransactions(tx)
            .TestObject;

        await ctx.Test.AddBlock(tx);

        LogEntry[] entries = new[]
        {
            Build.A.LogEntry.TestObject,
        };

        TxReceipt receipt = Build.A.Receipt.WithBloom(new Bloom(entries, new Bloom())).WithAllFieldsFilled
            .WithLogs(entries).TestObject;
        TxReceipt[] receiptsTab = { receipt };

        blockFinder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
        receiptFinder.Get(Arg.Any<Block>()).Returns(receiptsTab);
        receiptFinder.Get(Arg.Any<Keccak>()).Returns(receiptsTab);
        blockchainBridge.GetReceiptAndGasInfo(Arg.Any<Keccak>()).Returns((receipt, new(UInt256.One), 0));

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockFinder).WithReceiptFinder(receiptFinder).WithBlockchainBridge(blockchainBridge).Build();
        string serialized = ctx.Test.TestEthRpc("eth_getTransactionReceipt", tx.Hash!.ToString());

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0xda6b4df2595675cbee0d4889f41c3d0790204e8ed1b8ad4cadaa45a7d50dace5\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"cumulativeGasUsed\":\"0x3e8\",\"gasUsed\":\"0x64\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x0\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"root\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"status\":\"0x1\",\"error\":\"error\",\"type\":\"0x0\"},\"id\":67}"));
    }

    [Test]
    [Ignore("This test is flaky on CI. It could be connected with timeouts in block production.")]
    public async Task Eth_getTransactionReceipt_return_info_about_mined_1559tx()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        await ctx.Test.AddFundsAfterLondon((new Address("0x723847c97bc651c7e8c013dbbe65a70712f02ad3"), 1.Ether()));
        Transaction tx = Build.A.Transaction.WithData(new byte[] { 0, 1 })
            .SignedAndResolved().WithChainId(TestBlockchainIds.ChainId).WithGasPrice(0).WithValue(0).WithGasLimit(210200)
            .WithType(TxType.EIP1559).WithMaxFeePerGas(20.GWei()).WithMaxPriorityFeePerGas(1.GWei()).TestObject;
        await ctx.Test.AddBlock(tx);
        string serialized = ctx.Test.TestEthRpc("eth_getTransactionReceipt", tx.Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0x31501f80bf2ec493c368a519cb8ed6f132f0be26202304bbf1e1728642affb7f\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0x54515a11aa6c392ee2e1071fca3a579bc9a520930ef757dbf9b7d85fe155c691\",\"blockNumber\":\"0x5\",\"cumulativeGasUsed\":\"0x521c\",\"gasUsed\":\"0x521c\",\"effectiveGasPrice\":\"0x5e91eb5d\",\"from\":\"0x723847c97bc651c7e8c013dbbe65a70712f02ad3\",\"to\":\"0x0000000000000000000000000000000000000000\",\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x1\",\"type\":\"0x2\"},\"id\":67}"));
    }

    [Test]
    [Ignore("This test is flaky on CI. It could be connected with timeouts in block production.")]
    public async Task Eth_getTransactionByHash_return_info_about_mined_1559tx()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        await ctx.Test.AddFundsAfterLondon((new Address("0x723847c97bc651c7e8c013dbbe65a70712f02ad3"), 1.Ether()));
        Transaction tx = Build.A.Transaction.WithData(new byte[] { 0, 1 })
            .SignedAndResolved().WithChainId(TestBlockchainIds.ChainId).WithGasPrice(0).WithValue(0).WithGasLimit(210200)
            .WithType(TxType.EIP1559).WithMaxFeePerGas(20.GWei()).WithMaxPriorityFeePerGas(1.GWei()).TestObject;
        await ctx.Test.AddBlock(tx);
        string serialized = ctx.Test.TestEthRpc("eth_getTransactionByHash", tx.Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"hash\":\"0x31501f80bf2ec493c368a519cb8ed6f132f0be26202304bbf1e1728642affb7f\",\"nonce\":\"0x0\",\"blockHash\":\"0x54515a11aa6c392ee2e1071fca3a579bc9a520930ef757dbf9b7d85fe155c691\",\"blockNumber\":\"0x5\",\"transactionIndex\":\"0x0\",\"from\":\"0x723847c97bc651c7e8c013dbbe65a70712f02ad3\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x0\",\"gasPrice\":\"0x5e91eb5d\",\"maxPriorityFeePerGas\":\"0x3b9aca00\",\"maxFeePerGas\":\"0x4a817c800\",\"gas\":\"0x33518\",\"data\":\"0x0001\",\"input\":\"0x0001\",\"chainId\":\"0x1\",\"type\":\"0x2\",\"v\":\"0x0\",\"s\":\"0x6b82095065a599e6b5e52bed0043702baf3411418af679ac483f9fc75a8f6aef\",\"r\":\"0x8654517f7822e7a4e10e79f3f5a4136703c7d1b51d98e47686e201c3c2845f92\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_chain_id()
    {
        using Context ctx = await Context.Create();
        string serialized = ctx.Test.TestEthRpc("eth_chainId");
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"0x{TestBlockchainIds.ChainId:X}\",\"id\":67}}"));
    }

    [Test]
    public async Task Send_transaction_with_signature_will_not_try_to_sign()
    {
        using Context ctx = await Context.Create();
        ITxSender txSender = Substitute.For<ITxSender>();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        txSender.SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.PersistentBroadcast).Returns((TestItem.KeccakA, AcceptTxResult.Accepted));

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).WithTxSender(txSender).Build();
        Transaction tx = Build.A.Transaction.Signed(new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
        string serialized = ctx.Test.TestEthRpc("eth_sendRawTransaction", Rlp.Encode(tx, RlpBehaviors.None).Bytes.ToHexString());

        await txSender.Received().SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.PersistentBroadcast);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{TestItem.KeccakA.Bytes.ToHexString(true)}\",\"id\":67}}"));
    }

    [TestCase("f865808506fc23ac00830124f8940000000000000000000000000000000000000316018032a044b25a8b9b247d01586b3d59c71728ff49c9b84928d9e7fa3377ead3b5570b5da03ceac696601ff7ee6f5fe8864e2998db9babdf5eeba1a0cd5b4d44b3fcbd181b")]
    public async Task Send_raw_transaction_will_send_transaction(string rawTransaction)
    {
        using Context ctx = await Context.Create();
        ITxSender txSender = Substitute.ForPartsOf<TxPoolSender>(ctx.Test.TxPool, ctx.Test.TxSealer,
            ctx.Test.NonceManager, ctx.Test.EthereumEcdsa);
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).WithTxSender(txSender).Build();
        string serialized = ctx.Test.TestEthRpc("eth_sendRawTransaction", rawTransaction);
        Transaction tx = Rlp.Decode<Transaction>(Bytes.FromHexString(rawTransaction));
        await txSender.Received().SendTransaction(tx, TxHandlingOptions.PersistentBroadcast);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32010,\"message\":\"Invalid\"},\"id\":67}"));
    }

    [Test]
    public async Task Send_transaction_without_signature_will_not_set_nonce_when_zero_and_not_null()
    {
        using Context ctx = await Context.Create();
        ITxSender txSender = Substitute.For<ITxSender>();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        txSender.SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.PersistentBroadcast)
            .Returns((TestItem.KeccakA, AcceptTxResult.Accepted));

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithBlockchainBridge(bridge).WithTxSender(txSender).Build();
        Transaction tx = Build.A.Transaction.TestObject;
        TransactionForRpc rpcTx = new(tx);
        rpcTx.Nonce = 0;
        string serialized = ctx.Test.TestEthRpc("eth_sendTransaction", new EthereumJsonSerializer().Serialize(rpcTx));
        // TODO: actual test missing now
        await txSender.Received().SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.PersistentBroadcast);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{TestItem.KeccakA.Bytes.ToHexString(true)}\",\"id\":67}}"));
    }

    [Test]
    public async Task Send_transaction_without_signature_will_manage_nonce_when_null()
    {
        using Context ctx = await Context.Create();
        ITxSender txSender = Substitute.For<ITxSender>();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        txSender.SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce)
            .Returns((TestItem.KeccakA, AcceptTxResult.Accepted));

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithBlockchainBridge(bridge).WithTxSender(txSender).Build();
        Transaction tx = Build.A.Transaction.TestObject;
        TransactionForRpc rpcTx = new(tx);
        rpcTx.Nonce = null;
        string serialized = ctx.Test.TestEthRpc("eth_sendTransaction", new EthereumJsonSerializer().Serialize(rpcTx));

        await txSender.Received().SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{TestItem.KeccakA.Bytes.ToHexString(true)}\",\"id\":67}}"));
    }

    [Test]
    public async Task Send_transaction_should_return_ErrorCode_if_tx_not_added()
    {
        using Context ctx = await Context.Create();
        Transaction tx = Build.A.Transaction.WithValue(10000).SignedAndResolved(new PrivateKey("0x0000000000000000000000000000000000000000000000000000000000000001")).WithNonce(0).TestObject;
        TransactionForRpc txForRpc = new(tx);

        string serialized = ctx.Test.TestEthRpc("eth_sendTransaction", new EthereumJsonSerializer().Serialize(txForRpc));

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32010,\"message\":\"InsufficientFunds, Balance is zero, cannot pay gas\"},\"id\":67}"));
    }

    public enum AccessListProvided
    {
        None,
        Partial,
        Full
    }

    [TestCase(AccessListProvided.None, false, 2, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0xfffffffffffffffffffffffffffffffffffffffe\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]},{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0xf71b\"},\"id\":67}")]
    [TestCase(AccessListProvided.Full, false, 2, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0xfffffffffffffffffffffffffffffffffffffffe\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]},{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0xf71b\"},\"id\":67}")]
    [TestCase(AccessListProvided.Partial, false, 2, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]},{\"address\":\"0xfffffffffffffffffffffffffffffffffffffffe\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]}],\"gasUsed\":\"0xf71b\"},\"id\":67}")]

    [TestCase(AccessListProvided.None, true, 2, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0xee83\"},\"id\":67}")]
    [TestCase(AccessListProvided.Full, true, 2, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0xee83\"},\"id\":67}")]
    [TestCase(AccessListProvided.Partial, true, 2, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0xee83\"},\"id\":67}")]

    [TestCase(AccessListProvided.None, true, AccessTxTracer.MaxStorageAccessToOptimize, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0x14289\"},\"id\":67}")]
    [TestCase(AccessListProvided.Full, true, AccessTxTracer.MaxStorageAccessToOptimize, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0x14289\"},\"id\":67}")]
    [TestCase(AccessListProvided.Partial, true, AccessTxTracer.MaxStorageAccessToOptimize, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0x14289\"},\"id\":67}")]

    [TestCase(AccessListProvided.None, true, AccessTxTracer.MaxStorageAccessToOptimize + 5, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0xfffffffffffffffffffffffffffffffffffffffe\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\",\"0x0000000000000000000000000000000000000000000000000000000000000003\",\"0x0000000000000000000000000000000000000000000000000000000000000004\",\"0x0000000000000000000000000000000000000000000000000000000000000005\",\"0x0000000000000000000000000000000000000000000000000000000000000006\",\"0x0000000000000000000000000000000000000000000000000000000000000007\",\"0x0000000000000000000000000000000000000000000000000000000000000008\",\"0x0000000000000000000000000000000000000000000000000000000000000009\",\"0x000000000000000000000000000000000000000000000000000000000000000a\",\"0x000000000000000000000000000000000000000000000000000000000000000b\",\"0x000000000000000000000000000000000000000000000000000000000000000c\",\"0x000000000000000000000000000000000000000000000000000000000000000d\",\"0x000000000000000000000000000000000000000000000000000000000000000e\",\"0x000000000000000000000000000000000000000000000000000000000000000f\",\"0x0000000000000000000000000000000000000000000000000000000000000010\",\"0x0000000000000000000000000000000000000000000000000000000000000011\"]},{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0x16f48\"},\"id\":67}")]
    [TestCase(AccessListProvided.Full, true, AccessTxTracer.MaxStorageAccessToOptimize + 5, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0xfffffffffffffffffffffffffffffffffffffffe\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\",\"0x0000000000000000000000000000000000000000000000000000000000000003\",\"0x0000000000000000000000000000000000000000000000000000000000000004\",\"0x0000000000000000000000000000000000000000000000000000000000000005\",\"0x0000000000000000000000000000000000000000000000000000000000000006\",\"0x0000000000000000000000000000000000000000000000000000000000000007\",\"0x0000000000000000000000000000000000000000000000000000000000000008\",\"0x0000000000000000000000000000000000000000000000000000000000000009\",\"0x000000000000000000000000000000000000000000000000000000000000000a\",\"0x000000000000000000000000000000000000000000000000000000000000000b\",\"0x000000000000000000000000000000000000000000000000000000000000000c\",\"0x000000000000000000000000000000000000000000000000000000000000000d\",\"0x000000000000000000000000000000000000000000000000000000000000000e\",\"0x000000000000000000000000000000000000000000000000000000000000000f\",\"0x0000000000000000000000000000000000000000000000000000000000000010\",\"0x0000000000000000000000000000000000000000000000000000000000000011\"]},{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0x16f48\"},\"id\":67}")]
    [TestCase(AccessListProvided.Partial, true, AccessTxTracer.MaxStorageAccessToOptimize + 5, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]},{\"address\":\"0xfffffffffffffffffffffffffffffffffffffffe\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\",\"0x0000000000000000000000000000000000000000000000000000000000000003\",\"0x0000000000000000000000000000000000000000000000000000000000000004\",\"0x0000000000000000000000000000000000000000000000000000000000000005\",\"0x0000000000000000000000000000000000000000000000000000000000000006\",\"0x0000000000000000000000000000000000000000000000000000000000000007\",\"0x0000000000000000000000000000000000000000000000000000000000000008\",\"0x0000000000000000000000000000000000000000000000000000000000000009\",\"0x000000000000000000000000000000000000000000000000000000000000000a\",\"0x000000000000000000000000000000000000000000000000000000000000000b\",\"0x000000000000000000000000000000000000000000000000000000000000000c\",\"0x000000000000000000000000000000000000000000000000000000000000000d\",\"0x000000000000000000000000000000000000000000000000000000000000000e\",\"0x000000000000000000000000000000000000000000000000000000000000000f\",\"0x0000000000000000000000000000000000000000000000000000000000000010\",\"0x0000000000000000000000000000000000000000000000000000000000000011\"]}],\"gasUsed\":\"0x16f48\"},\"id\":67}")]

    public async Task Eth_create_access_list_sample(AccessListProvided accessListProvided, bool optimize, long loads, string expected)
    {
        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(new TestSpecProvider(Berlin.Instance));

        (byte[] code, AccessListItemForRpc[] _) = GetTestAccessList(loads);

        TransactionForRpc transaction = test.JsonSerializer.Deserialize<TransactionForRpc>($"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}");

        if (accessListProvided != AccessListProvided.None)
        {
            transaction.AccessList = GetTestAccessList(2, accessListProvided == AccessListProvided.Full).AccessList;
        }

        string serialized = test.TestEthRpc("eth_createAccessList", test.JsonSerializer.Serialize(transaction), "0x0", optimize.ToString().ToLower());
        Assert.That(serialized, Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase(0)]
    public static void Should_handle_gasCap_as_max_if_null_or_zero(long? gasCap)
    {
        TransactionForRpc rpcTx = new TransactionForRpc();

        rpcTx.EnsureDefaults(gasCap);

        Assert.That(rpcTx.Gas, Is.EqualTo(long.MaxValue), "Gas must be set to max if gasCap is null or 0");
    }

    [Test]
    public async Task eth_getBlockByNumber_should_return_withdrawals_correctly()
    {
        using Context ctx = await Context.Create();
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();

        Block block = Build.A.Block.WithNumber(1)
            .WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"))
            .WithTransactions(new[] { Build.A.Transaction.TestObject })
            .WithWithdrawals(new[] { Build.A.Withdrawal.WithAmount(1_000).TestObject })
            .TestObject;

        LogEntry[] entries = new[]
        {
            Build.A.LogEntry.TestObject,
            Build.A.LogEntry.TestObject
        };

        TxReceipt receipt = Build.A.Receipt.WithBloom(new Bloom(entries, new Bloom())).WithAllFieldsFilled
            .WithSender(TestItem.AddressE)
            .WithLogs(entries).TestObject;
        TxReceipt[] receiptsTab = { receipt };
        blockFinder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
        receiptFinder.Get(Arg.Any<Block>()).Returns(receiptsTab);
        receiptFinder.Get(Arg.Any<Keccak>()).Returns(receiptsTab);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockFinder).WithReceiptFinder(receiptFinder).Build();
        string result = ctx.Test.TestEthRpc("eth_getBlockByNumber", TestItem.KeccakA.ToString(), "true");

        result.Should().Be(new EthereumJsonSerializer().Serialize(new
        {
            jsonrpc = "2.0",
            result = new BlockForRpc(block, true, Substitute.For<ISpecProvider>()),
            id = 67
        }));
    }

    private static (byte[] ByteCode, AccessListItemForRpc[] AccessList) GetTestAccessList(long loads = 2, bool allowSystemUser = true)
    {
        AccessListItemForRpc[] accessList = allowSystemUser
            ? new[] {
                new AccessListItemForRpc(Address.SystemUser, Enumerable.Range(1, (int)loads).Select(i => (UInt256)i).ToArray()),
                new AccessListItemForRpc(TestItem.AddressC, Array.Empty<UInt256>()),
            }
            : new[] { new AccessListItemForRpc(TestItem.AddressC, Array.Empty<UInt256>()) };

        Prepare code = Prepare.EvmCode;

        for (int i = 1; i <= loads; i++)
        {
            // accesses Address.SystemUser with storage
            code = code.PushData(i)
                .Op(Instruction.SLOAD);
        }

        byte[] byteCode = code
            // accesses TestItem.AddressC without storage
            .PushData(TestItem.AddressC)
            .Op(Instruction.BALANCE)
            // return
            .PushData(new byte[] { 1, 2, 3 }.PadRight(32))
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(3)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;
        return (byteCode, accessList);
    }


    protected class Context : IDisposable
    {
        public TestRpcBlockchain Test { get; set; } = null!;
        public TestRpcBlockchain AuraTest { get; set; } = null!;

        private Context() { }

        public static async Task<Context> CreateWithLondonEnabled()
        {
            OverridableReleaseSpec releaseSpec = new(London.Instance) { Eip1559TransitionBlock = 1 };
            TestSpecProvider specProvider = new(releaseSpec);
            return await Create(specProvider);
        }

        public static async Task<Context> Create(ISpecProvider? specProvider = null, IBlockchainBridge? blockchainBridge = null) =>
            new()
            {
                Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(blockchainBridge!).Build(specProvider),
                AuraTest = await TestRpcBlockchain.ForTest(SealEngineType.AuRa).Build(specProvider)
            };

        public void Dispose()
        {
            Test?.Dispose();
            AuraTest?.Dispose();
        }
    }
}
