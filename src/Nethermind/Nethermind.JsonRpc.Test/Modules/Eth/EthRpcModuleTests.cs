// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Test.Json;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.TxPool;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

[Parallelizable(ParallelScope.Self)]
public partial class EthRpcModuleTests
{
    private const string BatTokenAddress = "0x0d8775f648430679a709e98d2b0cb6250d2887ef";
    private const string TestAccountAddress = "0x0001020304050607080910111213141516171819";
    private const string SecondaryTestAddress = "0x32e4e4c7c5d1cea5db5f9202a9e4d99e56c91a24";
    private const string BalanceOfCallData = "0x70a082310000000000000000000000006c1f09f6271fbe133db38db9c9280307f5d22160";
    private const string CreateAccessListSender = "0x7f554713be84160fdf0178cc8df86f5aabd33397";
    private const string ExpectedHeadTxRawAtIndex1 = "0xf85f020182520894942921b14f1b1c385cd7e0cc2ef7abe5598c8358018025a0e7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bda0575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb";
    private const string ExpectedFilterLogResponse = """{"jsonrpc":"2.0","result":[{"address":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","blockHash":"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760","blockNumber":"0x1","blockTimestamp":"0x1","data":"0x010203","logIndex":"0x1","removed":false,"topics":["0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72","0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2"],"transactionHash":"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111","transactionIndex":"0x1"}],"id":67}""";
    private const int LogsStreamEnvelopeEndReserveBytes = 128;
    private const int TimeoutCancellationTokenPoolSize = 64;

    private static string ExpectedFilterLogStreamResponse(string status) =>
        ExpectedFilterLogResponse.Replace(",\"id\":67}", $",\"_streamStatus\":\"{status}\",\"id\":67}}");

    private static readonly Address TestAccount = new(TestAccountAddress);

    private static FilterLog CreateTestFilterLog() =>
        new(1, 1, 1, TestItem.KeccakA, 1, TestItem.KeccakB, TestItem.AddressA, [1, 2, 3], [TestItem.KeccakC, TestItem.KeccakD]);

    private static readonly byte[] InfiniteLoopCode = Prepare.EvmCode
        .Op(Instruction.JUMPDEST)
        .PushData(0)
        .Op(Instruction.JUMP)
        .Done;

    private static readonly byte[] BaseFeeReturnCode = Prepare.EvmCode
        .Op(Instruction.BASEFEE)
        .PushData(0)
        .Op(Instruction.MSTORE)
        .PushData("0x20")
        .PushData("0x0")
        .Op(Instruction.RETURN)
        .Done;

    private static readonly byte[] CoinbaseReturnCode = Prepare.EvmCode
        .Op(Instruction.COINBASE)
        .PushData(0)
        .Op(Instruction.MSTORE)
        .PushData("0x20")
        .PushData("0x0")
        .Op(Instruction.RETURN)
        .Done;

    private static void AssertAccountDoesNotExist(Context ctx, Address account) => Assert.That(ctx.Test.ReadOnlyState.AccountExists(account), Is.False);

    [TestCase("earliest", "0x3635c9adc5dea00000")]
    [TestCase("latest", "0x3635c9adc5de9f09e5")]
    [TestCase("pending", "0x3635c9adc5de9f09e5")]
    [TestCase("0x0", "0x3635c9adc5dea00000")]
    public async Task Eth_get_balance(string blockParameter, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true), blockParameter);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\",\"id\":67}}"));
    }

    [Test]
    public async Task Eth_get_balance_default_block()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true));
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x3635c9adc5de9f09e5\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_eth_feeHistory()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_feeHistory", "0x1", "latest", "[20,50,90]");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"baseFeePerGas\":[\"0x0\",\"0x0\"],\"baseFeePerBlobGas\":[\"0x0\",\"0x0\"],\"gasUsedRatio\":[0.0105],\"blobGasUsedRatio\":[0],\"oldestBlock\":\"0x3\",\"reward\":[[\"0x1\",\"0x1\",\"0x1\"]]},\"id\":67}"));
    }

    [Test]
    public async Task EthFeeHistory_WhenRewardPercentilesIsMissing_ReturnsInvalidParams()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_feeHistory", "0x1", "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"missing value for required argument 2\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_transaction_by_block_hash_and_index()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionByBlockHashAndIndex", ctx.Test.BlockTree.FindHeadBlock()!.Hash!.ToString(), "1");
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse("""{"jsonrpc":"2.0","result":{"hash":"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b","nonce":"0x2","blockHash":"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3","blockNumber":"0x3","blockTimestamp":"0x5e47e919","transactionIndex":"0x1","from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","to":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358","value":"0x1","gasPrice":"0x1","gas":"0x5208","input":"0x","chainId":"0x1","type":"0x0","v":"0x25","s":"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb","r":"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd"},"id":67}""")).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Eth_get_transaction_by_hash()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionByHash", ctx.Test.BlockTree.FindHeadBlock()!.Transactions.Last().Hash!.ToString());
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse("""{"jsonrpc":"2.0","result":{"hash":"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b","nonce":"0x2","blockHash":"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3","blockNumber":"0x3","blockTimestamp":"0x5e47e919","transactionIndex":"0x1","from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","to":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358","value":"0x1","gasPrice":"0x1","gas":"0x5208","input":"0x","chainId":"0x1","type":"0x0","v":"0x25","s":"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb","r":"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd"},"id":67}""")).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Eth_get_raw_transaction_by_hash()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getRawTransactionByHash", ctx.Test.BlockTree.FindHeadBlock()!.Transactions.Last().Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0xf85f020182520894942921b14f1b1c385cd7e0cc2ef7abe5598c8358018025a0e7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bda0575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"id\":67}"), serialized.Replace("\"", "\\\""));
    }


    [Test]
    public async Task Eth_get_raw_transaction_by_hash_for_typed_transaction()
    {
        using Context ctx = await Context.CreateWithCancunEnabled();
        await ctx.Test.AddBlock(Build.A.Transaction.WithMaxPriorityFeePerGas(6.GWei).WithMaxFeePerGas(11.GWei).WithType(TxType.EIP1559).SignedAndResolved(TestItem.PrivateKeyC).TestObject);
        string serialized = await ctx.Test.TestEthRpc("eth_getRawTransactionByHash", ctx.Test.BlockTree.FindHeadBlock()!.Transactions.Last().Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x02f86c0180850165a0bc0085028fa6ae008252089400000000000000000000000000000000000000000180c080a063b08cc0a06c88fb1dd79f273736b3463af12c6754f9df764aa222d2693a5d43a0606b869eab1c9d01ff462f887826cb8f349ea8f1b59d0635ae77155b3b84ad86\",\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_raw_transaction_by_hash_from_pool()
    {
        using Context ctx = await Context.CreateWithCancunEnabled();
        Transaction sent = Build.A.Transaction.WithShardBlobTxTypeAndFields().WithMaxPriorityFeePerGas(6.GWei).WithMaxFeePerGas(11.GWei).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
        ctx.Test.TxPool.SubmitTx(sent, TxHandlingOptions.None);

        string serialized = await ctx.Test.TestEthRpc("eth_getRawTransactionByHash", sent.Hash);
        byte[]? txBytes = new EthereumJsonSerializer().Deserialize<JsonRpcResponse<byte[]>>(serialized)!.Result;

        Assert.That(txBytes, Is.Not.Null);
        RlpReader context = new(txBytes!);
        Transaction tx = TxDecoder.Instance.DecodeGuardNotNull(ref context, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
        Assert.That(tx.IsInMempoolForm(), Is.True);
    }

    [TestCaseSource(nameof(EthGetRawTransactionByBlockAndIndexCases))]
    public async Task EthGetRawTransactionByBlockAndIndex(string method, string? blockOverride, string index, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string blockArg = blockOverride ?? ctx.Test.BlockTree.FindHeadBlock()!.Hash!.ToString();
        string serialized = await ctx.Test.TestEthRpc(method, blockArg, index);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedResult},\"id\":67}}"));
    }

    private static IEnumerable<TestCaseData> EthGetRawTransactionByBlockAndIndexCases()
    {
        string raw = $"\"{ExpectedHeadTxRawAtIndex1}\"";
        yield return new TestCaseData("eth_getRawTransactionByBlockHashAndIndex", (string?)null, "1", raw)
            .SetName("ByHashValidIndex");
        yield return new TestCaseData("eth_getRawTransactionByBlockNumberAndIndex", "latest", "1", raw)
            .SetName("ByNumberValidIndex");
        yield return new TestCaseData("eth_getRawTransactionByBlockHashAndIndex", (string?)null, "99", "null")
            .SetName("IndexOutOfRange");
        yield return new TestCaseData("eth_getRawTransactionByBlockNumberAndIndex", "0x9999999", "0", "null")
            .SetName("BlockUnknown");
    }


    [Test]
    public async Task eth_maxPriorityFeePerGas_test()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_maxPriorityFeePerGas");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x1\",\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_pending_transactions()
    {
        using Context ctx = await Context.Create();
        ctx.Test.AddTransactions(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).TestObject);
        string serialized = await ctx.Test.TestEthRpc("eth_pendingTransactions");
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse("""{"jsonrpc":"2.0","result":[{"hash":"0x190d9a78dbc61b1856162ab909976a1b28ba4a41ee041341576ea69686cd3b29","nonce":"0x0","blockHash":"0x0000000000000000000000000000000000000000000000000000000000000000","blockNumber":null,"blockTimestamp":null,"transactionIndex":null,"from":"0x475674cb523a0a2736b7f7534390288fce16982c","to":"0x0000000000000000000000000000000000000000","value":"0x1","gasPrice":"0x1","gas":"0x5208","input":"0x","chainId":"0x1","type":"0x0","v":"0x26","s":"0x2d04e55699fa32e6b65a22189f7571f5030d636d7d44a8b53fe016a2c3ecde24","r":"0xda3978c3a1430bd902cf5bbca73c5a1eca019b3f003c95ee16657fd0bb89534c"}],"id":67}""")).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Eth_pending_transactions_1559_tx()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        ctx.Test.AddTransactions(Build.A.Transaction.WithMaxPriorityFeePerGas(6.GWei).WithMaxFeePerGas(11.GWei).WithType(TxType.EIP1559).SignedAndResolved(TestItem.PrivateKeyC).TestObject);
        string serialized = await ctx.Test.TestEthRpc("eth_pendingTransactions");
        Assert.That(JToken.Parse(serialized), Does.ContainSubtree("""{"result": [{"hash":"0x7544f95c68426cb8a8a5a54889c60849ed96ff317835beb63b4d745cbc078cec","nonce":"0x0","blockHash":"0x0000000000000000000000000000000000000000000000000000000000000000","blockNumber":null,"transactionIndex":null,"from":"0x76e68a8696537e4141926f3e528733af9e237d69","to":"0x0000000000000000000000000000000000000000","value":"0x1","gasPrice":"0x28fa6ae00","maxPriorityFeePerGas":"0x165a0bc00","maxFeePerGas":"0x28fa6ae00","gas":"0x5208","input":"0x","chainId":"0x1","type":"0x2","accessList":[],"v":"0x0","s":"0x606b869eab1c9d01ff462f887826cb8f349ea8f1b59d0635ae77155b3b84ad86","r":"0x63b08cc0a06c88fb1dd79f273736b3463af12c6754f9df764aa222d2693a5d43","yParity":"0x0"}]}"""));
    }

    [Test]
    public async Task Eth_pending_transactions_2930_tx()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        ctx.Test.AddTransactions(Build.A.Transaction.WithMaxPriorityFeePerGas(6.GWei).WithMaxFeePerGas(11.GWei).WithType(TxType.AccessList).SignedAndResolved(TestItem.PrivateKeyC).TestObject);
        string serialized = await ctx.Test.TestEthRpc("eth_pendingTransactions");
        Assert.That(JToken.Parse(serialized), Does.ContainSubtree("""{"result": [{"hash":"0x4eabe360dc515aadc8e35f75b23803bb86e7186ebf2e58412555b3d0c7750dcc","nonce":"0x0","blockHash":"0x0000000000000000000000000000000000000000000000000000000000000000","blockNumber":null,"transactionIndex":null,"from":"0x76e68a8696537e4141926f3e528733af9e237d69","to":"0x0000000000000000000000000000000000000000","value":"0x1","gasPrice":"0x165a0bc00","gas":"0x5208","input":"0x","chainId":"0x1","type":"0x1","accessList":[],"v":"0x0","s":"0x27e3dde7b07d6d6b50e0d11b29085036e9c8adc12dea52f6f07dd7a0551ff22a","r":"0x619cb31fd4aa1c38ae36b31c5d8310f74d9f8ddd94389db91a68deb26737f2dc","yParity":"0x0"}]}"""));
    }

    [Test]
    public async Task Eth_getBlockReceipts()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockReceipts", "latest");
        string expectedResult = "{\"jsonrpc\":\"2.0\",\"result\":[{\"transactionHash\":\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"cumulativeGasUsed\":\"0x5208\",\"gasUsed\":\"0x5208\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x1\",\"type\":\"0x0\"},{\"transactionHash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"transactionIndex\":\"0x1\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"cumulativeGasUsed\":\"0xa410\",\"gasUsed\":\"0x5208\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x1\",\"type\":\"0x0\"}],\"id\":67}";
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expectedResult)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Eth_get_transaction_by_block_number_and_index()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionByBlockNumberAndIndex", ctx.Test.BlockTree.FindHeadBlock()!.Number.ToHexString(true), "1");
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse("""{"jsonrpc":"2.0","result":{"hash":"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b","nonce":"0x2","blockHash":"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3","blockNumber":"0x3","blockTimestamp":"0x5e47e919","transactionIndex":"0x1","from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","to":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358","value":"0x1","gasPrice":"0x1","gas":"0x5208","input":"0x","chainId":"0x1","type":"0x0","v":"0x25","s":"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb","r":"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd"},"id":67}""")).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Eth_get_transaction_by_block_number_and_index_out_of_bounds()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionByBlockNumberAndIndex", ctx.Test.BlockTree.FindHeadBlock()!.Number.ToHexString(true), "100");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}"));
    }

    [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x0\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
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
        string serialized = await ctx.Test!.TestEthRpc("eth_getUncleByBlockNumberAndIndex", ctx.Test.BlockTree.FindHeadBlock()!.Number.ToHexString(true), "1");
        Assert.That(serialized, Is.EqualTo(expectedJson), serialized?.Replace("\"", "\\\""));
    }

    [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x0\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    public async Task Eth_get_uncle_by_block_hash_and_index(bool eip1559, string expectedJson)
    {
        ISpecProvider? specProvider = null;
        if (eip1559)
        {
            specProvider = Substitute.For<ISpecProvider>();
            ReleaseSpec releaseSpec = new() { IsEip1559Enabled = true, Eip1559TransitionBlock = 0 };
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(releaseSpec);
        }

        using Context ctx = await Context.Create();
        Block block = Build.A.Block.WithUncles(Build.A.BlockHeader.WithNumber(2).TestObject, Build.A.BlockHeader.TestObject).WithNumber(3).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindBlock((BlockParameter?)null).ReturnsForAnyArgs(block);
        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockTree).Build(specProvider);
        string serialized = await ctx.Test.TestEthRpc("eth_getUncleByBlockHashAndIndex", ctx.Test.BlockTree.FindHeadBlock()!.Hash!.ToString(), "1");
        Assert.That(serialized, Is.EqualTo(expectedJson), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_uncle_count_by_block_hash()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getUncleCountByBlockHash", ctx.Test.BlockTree.FindHeadBlock()!.Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_uncle_count_by_block_number()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getUncleCountByBlockNumber", ctx.Test.BlockTree.FindHeadBlock()!.Number.ToHexString(true));
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
            .SignedAndResolved(TestItem.PrivateKeyA).WithValue(0.Ether).WithNonce(3).TestObject;
        Transaction txWithFutureNonce = Build.A.Transaction.To(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA).WithValue(0.Ether).WithNonce(5).TestObject;
        ValueTask<(Hash256? Hash, AcceptTxResult? AddTxResult)> resultNextNonce =
            ctx.Test.TxSender.SendTransaction(txWithNextNonce, TxHandlingOptions.None)!;
        ValueTask<(Hash256? Hash, AcceptTxResult? AddTxResult)> resultFutureNonce =
            ctx.Test.TxSender.SendTransaction(txWithFutureNonce, TxHandlingOptions.None)!;
        Assert.That(AcceptTxResult.Accepted, Is.EqualTo(resultNextNonce.Result.AddTxResult));
        Assert.That(AcceptTxResult.Accepted, Is.EqualTo(resultFutureNonce.Result.AddTxResult));

        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionCount", TestItem.AddressA.Bytes.ToHexString(true), blockParameter);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\",\"id\":67}}"));
    }

    [Test]
    public async Task Eth_get_tx_count_default_block()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionCount", TestItem.AddressA.Bytes.ToHexString(true));
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x3\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_tx_count_pending_block()
    {
        using Context ctx = await Context.Create();
        string serializedPendingBefore = await ctx.Test.TestEthRpc("eth_getTransactionCount", TestItem.AddressB.Bytes.ToHexString(true), "pending");
        Assert.That(serializedPendingBefore, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}"));
        Transaction txWithNextNonce = Build.A.Transaction.To(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyB).WithValue(0.Ether).WithNonce(0).TestObject;
        ValueTask<(Hash256? Hash, AcceptTxResult? AddTxResult)> resultNextNonce =
            ctx.Test.TxSender.SendTransaction(txWithNextNonce, TxHandlingOptions.None)!;
        Assert.That(AcceptTxResult.Accepted, Is.EqualTo(resultNextNonce.Result.AddTxResult));
        string serializedLatestAfter = await ctx.Test.TestEthRpc("eth_getTransactionCount", TestItem.AddressB.Bytes.ToHexString(true));
        Assert.That(serializedLatestAfter, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}"));
        string serializedPendingAfter = await ctx.Test.TestEthRpc("eth_getTransactionCount", TestItem.AddressB.Bytes.ToHexString(true), "pending");
        Assert.That(serializedPendingAfter, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x1\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_changes_empty()
    {
        using Context ctx = await Context.Create();
        _ = await ctx.Test.TestEthRpc("eth_newBlockFilter");
        string serialized2 = await ctx.Test.TestEthRpc("eth_getFilterChanges", "0");
        Assert.That(serialized2, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_changes_missing()
    {
        using Context ctx = await Context.Create();
        string serialized2 = await ctx.Test.TestEthRpc("eth_getFilterChanges", "0");
        Assert.That(serialized2, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"Filter not found\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_changes_too_big()
    {
        using Context ctx = await Context.Create();
        string serialized2 = await ctx.Test.TestEthRpc("eth_getFilterChanges", ((UInt256)uint.MaxValue + 1).ToHexString(true));
        Assert.That(serialized2, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"Filter not found\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_uninstall_filter()
    {
        using Context ctx = await Context.Create();
        _ = await ctx.Test.TestEthRpc("eth_newBlockFilter");
        string serialized2 = await ctx.Test.TestEthRpc("eth_uninstallFilter", "0");
        Assert.That(serialized2, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}"));
    }

    [Test]
    public async Task Eth_uninstall_filter_overflow_int()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_uninstallFilter", "0xd003d345df");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":false,\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_changes_with_block()
    {
        using Context ctx = await Context.Create();
        _ = await ctx.Test.TestEthRpc("eth_newBlockFilter");
        await ctx.Test.AddBlock();
        string serialized2 = await ctx.Test.TestEthRpc("eth_getFilterChanges", "0");

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
            .WithNonce(3).WithGasLimit(210200).WithGasPrice(20.GWei).TestObject;

        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(initialValues: 2.Ether);

        Hash256? blockHash = Keccak.Zero;
        void handleNewBlock(object? sender, BlockEventArgs e)
        {
            blockHash = e.Block.Hash;
            test.BlockTree.NewHeadBlock -= handleNewBlock;
        }
        test.BlockTree.NewHeadBlock += handleNewBlock;

        using JsonRpcResponse newFilterResp = await RpcTest.TestRequest(test.EthRpcModule, "eth_newFilter", new { fromBlock = "latest" });
        UInt256? filterId = RpcTest.AssertSuccess<UInt256?>(newFilterResp);
        string filterIdParameter = filterId?.ToString() ?? "0x0";
        string getFilterLogsSerialized1 = await test.TestEthRpc("eth_getFilterChanges", filterIdParameter);

        //expect empty - no changes so far
        Assert.That(getFilterLogsSerialized1, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"));

        await test.AddBlock(createCodeTx);

        //expect new transaction logs
        string getFilterLogsSerialized2 = await test.TestEthRpc("eth_getFilterChanges", filterIdParameter);
        Assert.That(getFilterLogsSerialized2, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[{\"address\":\"0x0ffd3e46594919c04bcfd4e146203c8255670828\",\"blockHash\":\"0xf9fc52a47b7da4e8227cd60e9c368fa7d44df7f3226d5163005eec015588d64b\",\"blockNumber\":\"0x4\",\"blockTimestamp\":\"0x5e47e91a\",\"data\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[],\"transactionHash\":\"0x8c9c109bff7969c8aed8e51ab4ea35c6f835a0c3266bc5c5721821a38cbf5445\",\"transactionIndex\":\"0x0\"}],\"id\":67}"));

        //expect empty - previous call cleans logs
        string getFilterLogsSerialized3 = await test.TestEthRpc("eth_getFilterChanges", filterIdParameter);
        Assert.That(getFilterLogsSerialized3, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_changes_with_tx()
    {
        using Context ctx = await Context.Create();
        _ = await ctx.Test.TestEthRpc("eth_newPendingTransactionFilter");
        ctx.Test.AddTransactions(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyD).TestObject);
        string serialized2 = await ctx.Test.TestEthRpc("eth_getFilterChanges", "0");

        Assert.That(serialized2, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[\"0x190d9a78dbc61b1856162ab909976a1b28ba4a41ee041341576ea69686cd3b29\"],\"id\":67}"), serialized2.Replace("\"", "\\\""));
    }

    [TestCase("earliest", "0x0000000000000000000000000000000000000000000000000000000000abcdef")]
    [TestCase("latest", "0x0000000000000000000000000000000000000000000000000000000000abcdef")]
    [TestCase("pending", "0x0000000000000000000000000000000000000000000000000000000000abcdef")]
    [TestCase("0x0", "0x0000000000000000000000000000000000000000000000000000000000abcdef")]
    public async Task Eth_get_storage_at(string blockParameter, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getStorageAt", TestItem.AddressA.Bytes.ToHexString(true), "0x1", blockParameter);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\",\"id\":67}}"));
    }

    [Test]
    public async Task Eth_get_storage_at_default_block()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getStorageAt", TestItem.AddressA.Bytes.ToHexString(true), "0x1");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0000000000000000000000000000000000000000000000000000000000abcdef\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_storage_at_accepts_leading_zero_key()
    {
        // The zero-padded form of slot 1 must resolve to the same slot as "0x1".
        using Context ctx = await Context.Create();
        string paddedKey = "0x0000000000000000000000000000000000000000000000000000000000000001";
        string serialized = await ctx.Test.TestEthRpc("eth_getStorageAt", TestItem.AddressA.Bytes.ToHexString(true), paddedKey, "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0000000000000000000000000000000000000000000000000000000000abcdef\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_storage_at_missing_trie_node()
    {
        // Asserts the patricia "missing trie node" error, which has no equivalent in the flat backend.
        using Context ctx = await Context.Create(useFlatDb: false);
        await Task.Delay(100); // Wait a bit for pruning
        ctx.Test.WorldStateManager.FlushCache(CancellationToken.None);
        ctx.Test.StateDb.Clear();
        BlockParameter? blockParameter = null;
        BlockHeader? header = ctx.Test.BlockFinder.FindHeader(blockParameter);
        string serialized = await ctx.Test.TestEthRpc("eth_getStorageAt", TestItem.AddressA.Bytes.ToHexString(true), "0x1");
        string expected = $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32000,\"message\":\"missing trie node {header?.StateRoot} (path ) state {header?.StateRoot} is not available\"}},\"id\":67}}";
        Assert.That(serialized, Is.EqualTo(expected));
    }

    private static IEnumerable<TestCaseData> EthGetStorageValuesCases()
    {
        string addressA = TestItem.AddressA.Bytes.ToHexString(true);
        string addressB = TestItem.AddressB.Bytes.ToHexString(true);
        string zero = "0x0000000000000000000000000000000000000000000000000000000000000000";
        string abcdef = "0x0000000000000000000000000000000000000000000000000000000000abcdef";

        yield return new TestCaseData(
                new Dictionary<Address, UInt256[]> { [TestItem.AddressA] = [UInt256.One] },
                $"{{\"jsonrpc\":\"2.0\",\"result\":{{\"{addressA}\":[\"{abcdef}\"]}},\"id\":67}}")
            .SetName("Eth_get_storage_values_WhenSingleSlotRequested_ReturnsPaddedStorageValue");

        yield return new TestCaseData(
                new Dictionary<Address, UInt256[]> { [TestItem.AddressA] = [UInt256.One, UInt256.Zero] },
                $"{{\"jsonrpc\":\"2.0\",\"result\":{{\"{addressA}\":[\"{abcdef}\",\"{zero}\"]}},\"id\":67}}")
            .SetName("Eth_get_storage_values_WhenMultipleSlotsRequested_ReturnsValuesInRequestOrder");

        yield return new TestCaseData(
                new Dictionary<Address, UInt256[]> { [TestItem.AddressB] = [UInt256.Zero] },
                $"{{\"jsonrpc\":\"2.0\",\"result\":{{\"{addressB}\":[\"{zero}\"]}},\"id\":67}}")
            .SetName("Eth_get_storage_values_WhenAccountHasNoStorage_ReturnsZeros");

        yield return new TestCaseData(
                new Dictionary<Address, UInt256[]>(),
                "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"empty request\"},\"id\":67}")
            .SetName("Eth_get_storage_values_WhenRequestDictionaryIsEmpty_ReturnsInvalidParams");

        yield return new TestCaseData(
                new Dictionary<Address, UInt256[]> { [TestItem.AddressA] = [] },
                "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"empty request\"},\"id\":67}")
            .SetName("Eth_get_storage_values_WhenAllSlotArraysAreEmpty_ReturnsInvalidParams");

        yield return new TestCaseData(
                new Dictionary<Address, UInt256[]> { [TestItem.AddressA] = Enumerable.Range(0, EthRpcModule.MaxGetStorageSlots + 1).Select(i => (UInt256)i).ToArray() },
                $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32602,\"message\":\"too many slots (max {EthRpcModule.MaxGetStorageSlots})\"}},\"id\":67}}")
            .SetName("Eth_get_storage_values_WhenSlotCountExceedsLimit_ReturnsInvalidParams");
    }

    [TestCaseSource(nameof(EthGetStorageValuesCases))]
    public async Task Eth_get_storage_values(Dictionary<Address, UInt256[]> requests, string expected)
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getStorageValues", requests, "latest");
        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public async Task Eth_get_storage_values_accepts_leading_zero_key()
    {
        using Context ctx = await Context.Create();
        string addressA = TestItem.AddressA.Bytes.ToHexString(true);
        Dictionary<Address, string[]> request = new() { [TestItem.AddressA] = ["0x0000000000000000000000000000000000000000000000000000000000000001"] };
        string serialized = await ctx.Test.TestEthRpc("eth_getStorageValues", request, "latest");
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"{addressA}\":[\"0x0000000000000000000000000000000000000000000000000000000000abcdef\"]}},\"id\":67}}"));
    }

    [Test]
    public async Task Eth_get_proof_accepts_leading_zero_key()
    {
        using Context ctx = await Context.Create();
        string address = TestItem.AddressA.Bytes.ToHexString(true);
        string canonical = await ctx.Test.TestEthRpc("eth_getProof", address, new[] { "0x1" }, "latest");
        string padded = await ctx.Test.TestEthRpc("eth_getProof", address, new[] { "0x0000000000000000000000000000000000000000000000000000000000000001" }, "latest");
        Assert.That(padded, Is.EqualTo(canonical));
    }

    [TestCase("earliest", TestName = "Eth_get_storage_values_WhenEarliestBlock_ReturnsStorageValue")]
    [TestCase("latest", TestName = "Eth_get_storage_values_WhenLatestBlock_ReturnsStorageValue")]
    [TestCase("pending", TestName = "Eth_get_storage_values_WhenPendingBlock_ReturnsStorageValue")]
    [TestCase("0x0", TestName = "Eth_get_storage_values_WhenBlockByNumber_ReturnsStorageValue")]
    public async Task Eth_get_storage_values_WhenBlockParameterProvided_ReturnsStorageValue(string blockParameter)
    {
        using Context ctx = await Context.Create();
        string addressA = TestItem.AddressA.Bytes.ToHexString(true);
        BlockHeader? latestHeader = ctx.Test.BlockFinder.FindHeader(new BlockParameter(BlockParameterType.Latest));
        Assert.That(latestHeader, Is.Not.Null, "precondition: blockchain must have a latest block with initialized state");
        Dictionary<Address, UInt256[]> requests = new() { [TestItem.AddressA] = [UInt256.One] };
        string serialized = await ctx.Test.TestEthRpc("eth_getStorageValues", requests, blockParameter);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{{\"{addressA}\":[\"0x0000000000000000000000000000000000000000000000000000000000abcdef\"]}},\"id\":67}}"));
    }

    [Test]
    public async Task Eth_get_block_number()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_blockNumber");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x3\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_balance_internal_error()
    {
        using Context ctx = await Context.Create();
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns((Block?)null);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockTree).Build();
        string serialized = await ctx.Test.TestEthRpc("eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true), "0x1");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Incorrect head block\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_balance_incorrect_parameters()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBalance", TestItem.KeccakA.Bytes.ToHexString(true), "0x1");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params\"},\"id\":67}"));
    }

    [TestCase("0xFFFFFFFFF")]
    [TestCase("0x99999999999999")]
    public async Task Eth_get_balance_future_block_returns_header_not_found(string blockParameter)
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBalance", TestItem.AddressA.Bytes.ToHexString(true), blockParameter);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"header not found\"},\"id\":67}"));
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

        string serialized = await ctx.Test.TestEthRpc("eth_syncing");

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

        string serialized = await ctx.Test.TestEthRpc("eth_syncing");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":false,\"id\":67}"));
    }

    private static TestRpcBlockchain.Builder<TestRpcBlockchain> CreateLogsTestBlockchainBuilder(bool enableLogsStreamMode)
        => TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithConfig(new JsonRpcConfig { EnableLogsStreamMode = enableLogsStreamMode });

    private static async Task<string> WriteLogsStreamableResponseAsync(LogsStreamableResult result, CancellationToken cancellationToken = default)
    {
        using JsonRpcSuccessResponse response = new() { Id = new JsonRpcId(67L), Result = result };

        return await WriteJsonRpcResponseAsync(response, cancellationToken);
    }

    private static LogsStreamableResult CreateLogsStreamableResult(
        IEnumerable<FilterLog> logs,
        long? maxLogsResponseBodySize = null,
        long? maxBatchResponseBodySize = null,
        CancellationTokenSource? timeout = null) =>
        new(logs, 0, enforceMaxLogs: false, maxLogsResponseBodySize, maxBatchResponseBodySize, timeout ?? new CancellationTokenSource(), default);

    private static IEnumerable<TestCaseData> LogsStreamStatusCases()
    {
        yield return new TestCaseData(
            "Truncated",
            """{"jsonrpc":"2.0","result":[],"_streamStatus":"truncated","id":67}""")
            .SetName($"{nameof(Eth_get_logs_stream_mode_writes_status)}_Truncated");
        yield return new TestCaseData(
            "Timeout",
            """{"jsonrpc":"2.0","result":[],"_streamStatus":"timeout","id":67}""")
            .SetName($"{nameof(Eth_get_logs_stream_mode_writes_status)}_Timeout");
        yield return new TestCaseData(
            "Failed",
            ExpectedFilterLogStreamResponse("failed"))
            .SetName($"{nameof(Eth_get_logs_stream_mode_writes_status)}_Failed");
    }

    private static async Task<string> WriteJsonRpcResponseAsync(JsonRpcResponse response, CancellationToken cancellationToken = default)
    {
        Pipe pipe = new();
        CountingPipeWriter writer = new(pipe.Writer);

        await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, cancellationToken);
        await writer.CompleteAsync();

        System.IO.Pipelines.ReadResult read = await pipe.Reader.ReadAsync();
        string serialized = Encoding.UTF8.GetString(read.Buffer.ToArray());
        pipe.Reader.AdvanceTo(read.Buffer.End);
        await pipe.Reader.CompleteAsync();
        return serialized;
    }

    private static TrackingCancellationTokenSource RentTrackingTimeoutSourceForNextRequest()
    {
        JsonRpcConfig config = new();
        List<CancellationTokenSource> rentedTimeouts = new(TimeoutCancellationTokenPoolSize);
        for (int i = 0; i < TimeoutCancellationTokenPoolSize; i++)
        {
            rentedTimeouts.Add(config.BuildTimeoutCancellationToken());
        }

        for (int i = 0; i < rentedTimeouts.Count; i++)
        {
            rentedTimeouts[i].Dispose();
        }

        TrackingCancellationTokenSource timeout = new();
        JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeout);
        return timeout;
    }

    private static void DisposeIfNotAlreadyObserved(TrackingCancellationTokenSource timeout)
    {
        if (timeout.DisposeCount == 0)
        {
            timeout.Dispose();
        }
    }

    private sealed class TrackingCancellationTokenSource : CancellationTokenSource
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref _disposeCount);
            }

            base.Dispose(disposing);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Eth_get_filter_logs(bool enableLogsStreamMode)
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.TryGetLogs(1, out Arg.Any<IEnumerable<FilterLog>?>(), Arg.Any<CancellationToken>())
            .Returns(static x =>
            {
                x[1] = new[] { CreateTestFilterLog() };
                return true;
            });

        ctx.Test = await CreateLogsTestBlockchainBuilder(enableLogsStreamMode).WithBlockchainBridge(bridge).Build();
        string serialized = await ctx.Test.TestEthRpc("eth_getFilterLogs", "0x1");

        string expected = enableLogsStreamMode ? ExpectedFilterLogStreamResponse("complete") : ExpectedFilterLogResponse;
        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public async Task Eth_get_filter_logs_respects_max_logs_per_response()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.TryGetLogs(1, out Arg.Any<IEnumerable<FilterLog>?>(), Arg.Any<CancellationToken>())
            .Returns(static x =>
            {
                x[1] = new[] { CreateTestFilterLog(), CreateTestFilterLog() };
                return true;
            });

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithBlockchainBridge(bridge)
            .WithConfig(new JsonRpcConfig { EnableLogsStreamMode = false, MaxLogsPerResponse = 1 })
            .Build();

        string serialized = await ctx.Test.TestEthRpc("eth_getFilterLogs", "0x1");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32005,\"message\":\"Too many logs requested. Max logs per response is 1.\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_logs_stream_mode_stops_before_reading_past_max_logs()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.TryGetLogs(1, out Arg.Any<IEnumerable<FilterLog>?>(), Arg.Any<CancellationToken>())
            .Returns(static x =>
            {
                x[1] = GetLogs();
                return true;
            });

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithBlockchainBridge(bridge)
            .WithConfig(new JsonRpcConfig { EnableLogsStreamMode = true, MaxLogsPerResponse = 1 })
            .Build();

        string serialized = await ctx.Test.TestEthRpc("eth_getFilterLogs", "0x1");

        Assert.That(serialized, Is.EqualTo(ExpectedFilterLogStreamResponse("truncated")));

        static IEnumerable<FilterLog> GetLogs()
        {
            yield return CreateTestFilterLog();
            throw new InvalidOperationException("Stream mode read past MaxLogsPerResponse.");
        }
    }

    [Test]
    public async Task Eth_get_filter_logs_filter_not_found()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.TryGetLogs(5, out Arg.Any<IEnumerable<FilterLog>?>(), Arg.Any<CancellationToken>())
                .Returns(static x => { x[1] = null; return false; });

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
        string serialized = await ctx.Test.TestEthRpc("eth_getFilterLogs", "0x5");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Filter with id: 5 does not exist.\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_filter_logs_filterId_overflow()
    {
        using Context ctx = await Context.Create();

        UInt256 filterId = int.MaxValue + (UInt256)10;

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        string filterIdHex = filterId.ToString("X").TrimStart('0');
        string serialized = await ctx.Test.TestEthRpc("eth_getFilterLogs", $"0x{filterIdHex}");

        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":-32603,\"message\":\"Filter with id: {filterId} does not exist.\"}},\"id\":67}}"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Eth_get_logs_get_filter_logs_same_result(bool enableLogsStreamMode)
    {
        byte[] logCreateCode = Prepare.EvmCode
                .PushData(32)
                .PushData(0)
                .Op(Instruction.LOG0)
                .Done;

        Transaction createCodeTx = Build.A.Transaction
            .SignedAndResolved(TestItem.PrivateKeyA).WithChainId(TestBlockchainIds.ChainId).WithGasPrice(2)
            .WithCode(logCreateCode)
            .WithNonce(3).WithGasLimit(210200).WithGasPrice(20.GWei).TestObject;

        TestRpcBlockchain? test = await CreateLogsTestBlockchainBuilder(enableLogsStreamMode).Build(initialValues: 2.Ether);

        Hash256? blockHash = Keccak.Zero;

        void HandleNewBlock(object? sender, BlockReplacementEventArgs e)
        {
            blockHash = e.Block.Hash;
            test.BlockTree.BlockAddedToMain -= HandleNewBlock;
        }
        test.BlockTree.BlockAddedToMain += HandleNewBlock;

        await test.AddBlock(createCodeTx);

        string getLogsSerialized = await test.TestEthRpc("eth_getLogs", $"{{\"blockHash\":\"{blockHash}\"}}");

        using JsonRpcResponse newFilterResp = await RpcTest.TestRequest(test.EthRpcModule, "eth_newFilter", new { blockHash = blockHash });
        UInt256? filterId = RpcTest.AssertSuccess<UInt256?>(newFilterResp);
        string getFilterLogsSerialized = await test.TestEthRpc("eth_getFilterLogs", filterId?.ToString() ?? "0x0");

        Assert.That(getFilterLogsSerialized, Is.EqualTo(getLogsSerialized));
    }

    private static IEnumerable<TestCaseData> EthGetLogsCases()
    {
        foreach ((string name, string parameter, string expected) in EthGetLogsCaseParameters())
        {
            yield return new TestCaseData(parameter, expected, false)
                .SetName($"{nameof(Eth_get_logs)}_{name}_Buffered");
            yield return new TestCaseData(parameter, expected, true)
                .SetName($"{nameof(Eth_get_logs)}_{name}_Stream");
        }
    }

    private static IEnumerable<(string Name, string Parameter, string Expected)> EthGetLogsCaseParameters()
    {
        yield return ("EmptyFilter", "{}", ExpectedFilterLogResponse);
        yield return (
            "ExplicitRangeAddressAndTopic",
            """{"fromBlock":"0x2","toBlock":"latest","address":"0x0000000000000000000000000000000000000001","topics":["0x0000000000000000000000000000000000000000000000000000000000000001"]}""",
            ExpectedFilterLogResponse);
        yield return (
            "EarliestToPendingAddressArray",
            """{"fromBlock":"earliest","toBlock":"pending","address":["0x0000000000000000000000000000000000000001", "0x0000000000000000000000000000000000000001"],"topics":["0x0000000000000000000000000000000000000000000000000000000000000001", "0x0000000000000000000000000000000000000000000000000000000000000002"]}""",
            ExpectedFilterLogResponse);
        yield return (
            "NestedTopics",
            """{"topics":[null, ["0x0000000000000000000000000000000000000000000000000000000000000001", "0x0000000000000000000000000000000000000000000000000000000000000002"]]}""",
            ExpectedFilterLogResponse);
        yield return (
            "FutureFromBlock",
            """{"fromBlock":"0x10","toBlock":"latest","address":"0x0000000000000000000000000000000000000001","topics":["0x0000000000000000000000000000000000000000000000000000000000000001"]}""",
            """{"jsonrpc":"2.0","error":{"code":-32602,"message":"requested block range is in the future"},"id":67}""");
        yield return (
            "ExplicitBlockRange",
            """{"fromBlock":"0x2","toBlock":"0x3","address":"0x0000000000000000000000000000000000000001","topics":["0x0000000000000000000000000000000000000000000000000000000000000001"]}""",
            ExpectedFilterLogResponse);
        yield return (
            "InvalidBlockRange",
            """{"fromBlock":"0x2","toBlock":"0x1","address":"0x0000000000000000000000000000000000000001","topics":["0x0000000000000000000000000000000000000000000000000000000000000001"]}""",
            """{"jsonrpc":"2.0","error":{"code":-32602,"message":"invalid block range params"},"id":67}""");
        yield return (
            "FutureBlockRange",
            """{"fromBlock":"0x11","toBlock":"0x12","address":"0x0000000000000000000000000000000000000001","topics":["0x0000000000000000000000000000000000000000000000000000000000000001"]}""",
            """{"jsonrpc":"2.0","error":{"code":-32602,"message":"requested block range is in the future"},"id":67}""");
    }

    [TestCaseSource(nameof(EthGetLogsCases))]
    public async Task Eth_get_logs(string parameter, string expected, bool enableLogsStreamMode)
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetLogs(Arg.Any<LogFilter>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
            .Returns([CreateTestFilterLog()]);
        bridge.FilterExists(1).Returns(true);

        ctx.Test = await CreateLogsTestBlockchainBuilder(enableLogsStreamMode).WithBlockchainBridge(bridge).Build();
        string serialized = await ctx.Test.TestEthRpc("eth_getLogs", parameter);

        string expectedResponse = enableLogsStreamMode && expected == ExpectedFilterLogResponse
            ? ExpectedFilterLogStreamResponse("complete")
            : expected;
        Assert.That(serialized, Is.EqualTo(expectedResponse));
    }

    [TestCase(2, """{"fromBlock":"0x0","toBlock":"0x3"}""", true, TestName = "range 4 exceeds limit 2 -> rejected")]
    [TestCase(4, """{"fromBlock":"0x0","toBlock":"0x3"}""", false, TestName = "range 4 within limit 4 -> allowed")]
    [TestCase(0, """{"fromBlock":"0x0","toBlock":"0x3"}""", false, TestName = "limit disabled -> allowed")]
    [TestCase(2, """{"toBlock":"0x3"}""", true, TestName = "fromBlock omitted -> Earliest (0x0), range 4 exceeds limit 2 -> rejected")]
    [TestCase(4, """{"toBlock":"0x3"}""", false, TestName = "fromBlock omitted -> Earliest (0x0), range 4 within limit 4 -> allowed")]
    [TestCase(2, """{"fromBlock":"0x0"}""", true, TestName = "toBlock omitted -> Latest (0x3), range 4 exceeds limit 2 -> rejected")]
    [TestCase(4, """{"fromBlock":"0x0"}""", false, TestName = "toBlock omitted -> Latest (0x3), range 4 within limit 4 -> allowed")]
    public async Task Eth_get_logs_enforces_max_block_depth(int maxBlockDepth, string parameter, bool shouldReject)
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetLogs(Arg.Any<LogFilter>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
            .Returns([CreateTestFilterLog()]);

        ctx.Test = await CreateLogsTestBlockchainBuilder(enableLogsStreamMode: false)
            .WithBlockchainBridge(bridge)
            .WithReceiptConfig(new ReceiptConfig { MaxBlockDepth = maxBlockDepth })
            .Build();

        string serialized = await ctx.Test.TestEthRpc("eth_getLogs", parameter);

        if (shouldReject)
        {
            Assert.That(serialized, Does.Contain($"\"code\":{ErrorCodes.InvalidParams}"));
            Assert.That(serialized, Does.Contain(nameof(IReceiptConfig.MaxBlockDepth)));
        }
        else
        {
            Assert.That(serialized, Is.EqualTo(ExpectedFilterLogResponse));
        }
    }

    [TestCase("eth_getLogs", "{}")]
    [TestCase("eth_getFilterLogs", "0x1")]
    public async Task Eth_logs_ignore_max_logs_response_body_size_when_stream_mode_disabled(string method, string parameter)
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetLogs(Arg.Any<LogFilter>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
            .Returns([CreateTestFilterLog()]);
        bridge.TryGetLogs(1, out Arg.Any<IEnumerable<FilterLog>?>(), Arg.Any<CancellationToken>())
            .Returns(static x =>
            {
                x[1] = new[] { CreateTestFilterLog() };
                return true;
            });

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithBlockchainBridge(bridge)
            .WithConfig(new JsonRpcConfig { EnableLogsStreamMode = false, MaxLogsResponseBodySize = 64 })
            .Build();

        string serialized = await ctx.Test.TestEthRpc(method, parameter);

        Assert.That(serialized, Is.EqualTo(ExpectedFilterLogResponse));
    }

    [Test]
    public async Task Eth_get_logs_stream_mode_stops_before_reading_past_max_logs()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetLogs(Arg.Any<LogFilter>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
            .Returns(static _ => GetLogs());
        bridge.FilterExists(1).Returns(true);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithBlockchainBridge(bridge)
            .WithConfig(new JsonRpcConfig { EnableLogsStreamMode = true, MaxLogsPerResponse = 1 })
            .Build();

        string serialized = await ctx.Test.TestEthRpc("eth_getLogs", "{}");

        Assert.That(serialized, Is.EqualTo(ExpectedFilterLogStreamResponse("truncated")));

        static IEnumerable<FilterLog> GetLogs()
        {
            yield return CreateTestFilterLog();
            throw new InvalidOperationException("Stream mode read past MaxLogsPerResponse.");
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Eth_get_logs_stream_mode_transfers_timeout_until_response_disposal()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        CancellationToken observedToken = default;
        bridge.GetLogs(Arg.Any<LogFilter>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observedToken = call.ArgAt<CancellationToken>(3);
                return new[] { CreateTestFilterLog() };
            });

        ctx.Test = await CreateLogsTestBlockchainBuilder(enableLogsStreamMode: true).WithBlockchainBridge(bridge).Build();
        TrackingCancellationTokenSource timeout = RentTrackingTimeoutSourceForNextRequest();

        try
        {
            CancellationToken expectedToken = timeout.Token;
            ResultWrapper<IEnumerable<FilterLog>> response = ctx.Test.EthRpcModule.eth_getLogs(new Filter());

            try
            {
                Assert.That(observedToken, Is.EqualTo(expectedToken));
                Assert.That(response.Data, Is.TypeOf<LogsStreamableResult>());
                Assert.That(timeout.DisposeCount, Is.EqualTo(0));
            }
            finally
            {
                response.Dispose();
            }

            Assert.That(timeout.DisposeCount, Is.EqualTo(1));
        }
        finally
        {
            DisposeIfNotAlreadyObserved(timeout);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Eth_get_logs_stream_mode_disposes_timeout_when_returning_before_transfer()
    {
        using Context ctx = await Context.Create();
        ctx.Test = await CreateLogsTestBlockchainBuilder(enableLogsStreamMode: true).Build();
        TrackingCancellationTokenSource timeout = RentTrackingTimeoutSourceForNextRequest();

        try
        {
            ResultWrapper<IEnumerable<FilterLog>> response = ctx.Test.EthRpcModule.eth_getLogs(new Filter
            {
                FromBlock = new BlockParameter(2),
                ToBlock = new BlockParameter(1)
            });

            try
            {
                Assert.That(response.Result.ResultType, Is.EqualTo(ResultType.Failure));
                Assert.That(response.Result.Error, Is.EqualTo("invalid block range params"));
                Assert.That(timeout.DisposeCount, Is.EqualTo(1));
            }
            finally
            {
                response.Dispose();
            }

            Assert.That(timeout.DisposeCount, Is.EqualTo(1));
        }
        finally
        {
            DisposeIfNotAlreadyObserved(timeout);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Eth_get_logs_stream_mode_disposes_timeout_after_enumeration_failure()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        CancellationToken observedToken = default;
        bridge.GetLogs(Arg.Any<LogFilter>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observedToken = call.ArgAt<CancellationToken>(3);
                return GetLogs();
            });

        ctx.Test = await CreateLogsTestBlockchainBuilder(enableLogsStreamMode: true).WithBlockchainBridge(bridge).Build();
        TrackingCancellationTokenSource timeout = RentTrackingTimeoutSourceForNextRequest();

        try
        {
            CancellationToken expectedToken = timeout.Token;
            ResultWrapper<IEnumerable<FilterLog>> response = ctx.Test.EthRpcModule.eth_getLogs(new Filter());
            response.Id = new JsonRpcId(67L);
            string serialized;

            try
            {
                Assert.That(observedToken, Is.EqualTo(expectedToken));
                serialized = await WriteJsonRpcResponseAsync(response);
                Assert.That(timeout.DisposeCount, Is.EqualTo(0));
            }
            finally
            {
                response.Dispose();
            }

            Assert.That(serialized, Is.EqualTo(ExpectedFilterLogStreamResponse("failed")));
            Assert.That(timeout.DisposeCount, Is.EqualTo(1));
        }
        finally
        {
            DisposeIfNotAlreadyObserved(timeout);
        }

        static IEnumerable<FilterLog> GetLogs()
        {
            yield return CreateTestFilterLog();
            throw new InvalidOperationException("Stream mode failed after a complete log item.");
        }
    }

    [Test]
    public async Task Eth_get_logs_stream_mode_stops_before_response_body_limit()
    {
        using LogsStreamableResult result = CreateLogsStreamableResult([CreateTestFilterLog()], maxLogsResponseBodySize: 64);
        using MemoryStream stream = new();
        CountingPipeWriter writer = new(PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)));

        await result.WriteToAsync(writer, CancellationToken.None);
        await writer.FlushAsync();
        await writer.CompleteAsync();

        Assert.That(Encoding.UTF8.GetString(stream.ToArray()), Is.EqualTo("[]"));
    }

    [TestCaseSource(nameof(LogsStreamStatusCases))]
    public async Task Eth_get_logs_stream_mode_writes_status(string statusCase, string expected)
    {
        string serialized = await WriteLogsStreamableResponseAsync(CreateLogsStreamableStatusResult(statusCase));

        Assert.That(serialized, Is.EqualTo(expected));

        static LogsStreamableResult CreateLogsStreamableStatusResult(string statusCase) =>
            statusCase switch
            {
                "Truncated" => CreateLogsStreamableResult([CreateTestFilterLog()], maxLogsResponseBodySize: 64),
                "Timeout" => CreateTimeoutResult(),
                "Failed" => CreateLogsStreamableResult(GetLogs()),
                _ => throw new ArgumentOutOfRangeException(nameof(statusCase), statusCase, null)
            };

        static LogsStreamableResult CreateTimeoutResult()
        {
            CancellationTokenSource timeout = new();
            timeout.Cancel();
            return CreateLogsStreamableResult([CreateTestFilterLog()], timeout: timeout);
        }

        static IEnumerable<FilterLog> GetLogs()
        {
            yield return CreateTestFilterLog();
            throw new InvalidOperationException("Stream mode failed after a complete log item.");
        }
    }

    [Test]
    public async Task Eth_get_logs_stream_mode_estimates_next_log_before_reading_it()
    {
        string logJson = JsonSerializer.Serialize(CreateTestFilterLog(), EthereumJsonSerializer.JsonOptions);
        long maxLogsResponseBodySize = 1 + Encoding.UTF8.GetByteCount(logJson) + LogsStreamEnvelopeEndReserveBytes;
        using LogsStreamableResult result = CreateLogsStreamableResult(GetLogs(), maxLogsResponseBodySize);
        using MemoryStream stream = new();
        CountingPipeWriter writer = new(PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)));

        await result.WriteToAsync(writer, CancellationToken.None);
        await writer.FlushAsync();
        await writer.CompleteAsync();

        Assert.That(Encoding.UTF8.GetString(stream.ToArray()), Is.EqualTo($"[{logJson}]"));

        static IEnumerable<FilterLog> GetLogs()
        {
            yield return CreateTestFilterLog();
            throw new InvalidOperationException("Stream mode read the next log after the prior-size estimate exceeded MaxLogsResponseBodySize.");
        }
    }

    [Test]
    public async Task Eth_get_logs_stream_mode_uses_batch_remaining_limit_for_batch_items()
    {
        string logJson = JsonSerializer.Serialize(CreateTestFilterLog(), EthereumJsonSerializer.JsonOptions);
        int logBytes = Encoding.UTF8.GetByteCount(logJson);
        long alreadyWrittenBatchBytes = 32;
        long maxBatchResponseBodySize = alreadyWrittenBatchBytes + 1 + logBytes + LogsStreamEnvelopeEndReserveBytes;
        using LogsStreamableResult result = CreateLogsStreamableResult(GetLogs(), maxLogsResponseBodySize: long.MaxValue, maxBatchResponseBodySize);
        using MemoryStream stream = new();
        CountingPipeWriter writer = new(PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)), alreadyWrittenBatchBytes);

        await result.WriteToAsync(writer, isBatch: true, cancellationToken: CancellationToken.None);
        await writer.FlushAsync();
        await writer.CompleteAsync();

        Assert.That(Encoding.UTF8.GetString(stream.ToArray()), Is.EqualTo($"[{logJson}]"));

        static IEnumerable<FilterLog> GetLogs()
        {
            yield return CreateTestFilterLog();
            throw new InvalidOperationException("Stream mode did not use remaining MaxBatchResponseBodySize before reading the next batch item log.");
        }
    }

    [Test]
    public async Task Eth_get_logs_stream_mode_uses_batch_limit_when_logs_body_limit_is_not_specified()
    {
        Assert.That(new JsonRpcConfig().MaxLogsResponseBodySize, Is.Null);

        using LogsStreamableResult result = CreateLogsStreamableResult([CreateTestFilterLog()], maxBatchResponseBodySize: 64);
        using MemoryStream stream = new();
        CountingPipeWriter writer = new(PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)));

        await result.WriteToAsync(writer, CancellationToken.None);
        await writer.FlushAsync();
        await writer.CompleteAsync();

        Assert.That(Encoding.UTF8.GetString(stream.ToArray()), Is.EqualTo("[]"));
    }

    [Test]
    public void Json_rpc_config_disables_logs_stream_mode_by_default() =>
        Assert.That(new JsonRpcConfig().EnableLogsStreamMode, Is.False);

    [Test]
    public async Task Eth_get_logs_cancellation()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetLogs(Arg.Any<LogFilter>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
            .Returns(static c =>
            {
                return GetLogs(c.ArgAt<CancellationToken>(3));

                [DoesNotReturn]
                static IEnumerable<FilterLog> GetLogs(CancellationToken ct)
                {
                    while (true)
                    {
                        Thread.Sleep(10);
                        ct.ThrowIfCancellationRequested();
                    }
                }
            });

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).WithConfig(new JsonRpcConfig() { Timeout = 50 }).Build();
        string serialized = await ctx.Test.TestEthRpc("eth_getLogs", "{}");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32016,\"message\":\"eth_getLogs request was canceled due to enabled timeout.\"},\"id\":67}"));
    }

    [TestCase("{\"fromBlock\":\"earliest\",\"toBlock\":\"latest\"}", "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":4444,\"message\":\"pruned history unavailable\"},\"id\":67}")]
    public async Task Eth_get_logs_with_resourceNotFound(string parameter, string expected)
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetLogs(Arg.Any<LogFilter>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>())
            .Throws(new ResourceNotFoundException("resource not found message"));
        bridge.FilterExists(1).Returns(true);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
        string serialized = await ctx.Test.TestEthRpc("eth_getLogs", parameter);

        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public async Task Eth_get_filter_logs_with_resourceNotFound()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.TryGetLogs(1, out Arg.Any<IEnumerable<FilterLog>?>(), Arg.Any<CancellationToken>())
            .Returns(static x =>
            {
                x[1] = ThrowsOnIteration();
                return true;
            });

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).Build();
        string serialized = await ctx.Test.TestEthRpc("eth_getFilterLogs", "0x1");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":4444,\"message\":\"pruned history unavailable\"},\"id\":67}"));

        static IEnumerable<FilterLog> ThrowsOnIteration()
        {
            yield return Throw();
            static FilterLog Throw() => throw new ResourceNotFoundException("resource not found");
        }
    }

    [Test]
    public async Task Eth_tx_count_by_hash()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockTransactionCountByHash", ctx.Test.BlockTree.Genesis!.Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_uncle_count_by_hash()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getUncleCountByBlockHash", ctx.Test.BlockTree.Genesis!.Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x0\",\"id\":67}"));
    }

    [TestCase("earliest", "\"0x0\"")]
    [TestCase("latest", "\"0x0\"")]
    [TestCase("pending", "\"0x0\"")]
    [TestCase("0x0", "\"0x0\"")]
    [TestCase("0xFFFFFFFF", "null")]
    public async Task Eth_uncle_count_by_number(string blockParameter, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getUncleCountByBlockNumber", blockParameter);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedResult},\"id\":67}}"));
    }

    [TestCase("earliest", "\"0x0\"")]
    [TestCase("latest", "\"0x2\"")]
    [TestCase("pending", "\"0x2\"")]
    [TestCase("0x0", "\"0x0\"")]
    [TestCase("0xFFFFFFFF", "null")]
    public async Task Eth_tx_count_by_number(string blockParameter, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockTransactionCountByNumber", blockParameter);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedResult},\"id\":67}}"));
    }

    [TestCase(false, false, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, false, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x16af125b31ba6f33725bffd77d8778121c8b24c3c29a9821d2fc15049a5bdcb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"nonce\":null,\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":\"0x0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"size\":\"0x21b\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":0,\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(false, true, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x0\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, true, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x16af125b31ba6f33725bffd77d8778121c8b24c3c29a9821d2fc15049a5bdcb6\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"nonce\":null,\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"signature\":\"0x0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"size\":\"0x21b\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"step\":0,\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x0\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    public async Task Eth_get_block_by_hash(bool aura, bool eip1559, string expected)
    {
        using Context ctx = eip1559 ? await Context.CreateWithLondonEnabled() : await Context.Create();
        TestRpcBlockchain testBlockchain = (aura ? ctx.AuraTest : ctx.Test);
        string serialized = await testBlockchain.TestEthRpc("eth_getBlockByHash", testBlockchain.BlockTree.Genesis!.Hash!.ToString(), "true");
        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public async Task Eth_get_block_by_hash_null()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockByHash", Keccak.Zero.ToString(), "true");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}"));
    }

    [TestCase("0x71eac5e72c3b64431c246173352a8c625c8434d944eb5f3f58204fec3ec36b54", false, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x49e7d7466be0927347ff2f654c014a768b5a5fcd8c483635210466dd0d6d204c\",\"receiptsRoot\":\"0xd95b673818fa493deec414e01e610d97ee287c9421c8eff4102b1647c1a184e4\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x4e786afc8bed76b7299973ca70022b367cbb94c14ec30e9e7273b31b6b968de9\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\"],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
    [TestCase("0x71eac5e72c3b64431c246173352a8c625c8434d944eb5f3f58204fec3ec36b54", true, "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x49e7d7466be0927347ff2f654c014a768b5a5fcd8c483635210466dd0d6d204c\",\"receiptsRoot\":\"0xd95b673818fa493deec414e01e610d97ee287c9421c8eff4102b1647c1a184e4\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x4e786afc8bed76b7299973ca70022b367cbb94c14ec30e9e7273b31b6b968de9\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[{\"hash\":\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"nonce\":\"0x1\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"blockTimestamp\":\"0x5e47e919\",\"transactionIndex\":\"0x0\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"input\":\"0x\",\"chainId\":\"0x1\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"blockTimestamp\":\"0x5e47e919\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"input\":\"0x\",\"chainId\":\"0x1\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"}],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
    public async Task Eth_get_block_by_hash_with_tx(string blockParameter, bool withTxData, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockByHash", ctx.Test.BlockTree.Head!.Hash!.ToString(), withTxData.ToString());
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expectedResult)).Using(JToken.EqualityComparer));
    }

    [TestCase(false, "earliest", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(false, "latest", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x49e7d7466be0927347ff2f654c014a768b5a5fcd8c483635210466dd0d6d204c\",\"receiptsRoot\":\"0xd95b673818fa493deec414e01e610d97ee287c9421c8eff4102b1647c1a184e4\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x4e786afc8bed76b7299973ca70022b367cbb94c14ec30e9e7273b31b6b968de9\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[{\"hash\":\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"nonce\":\"0x1\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"blockTimestamp\":\"0x5e47e919\",\"transactionIndex\":\"0x0\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"input\":\"0x\",\"chainId\":\"0x1\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"blockTimestamp\":\"0x5e47e919\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"input\":\"0x\",\"chainId\":\"0x1\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"}],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
    [TestCase(false, "pending", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":null,\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":null,\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":null,\"number\":\"0x3\",\"parentHash\":\"0x49e7d7466be0927347ff2f654c014a768b5a5fcd8c483635210466dd0d6d204c\",\"receiptsRoot\":\"0xd95b673818fa493deec414e01e610d97ee287c9421c8eff4102b1647c1a184e4\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x4e786afc8bed76b7299973ca70022b367cbb94c14ec30e9e7273b31b6b968de9\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[{\"hash\":\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"nonce\":\"0x1\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"blockTimestamp\":\"0x5e47e919\",\"transactionIndex\":\"0x0\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"input\":\"0x\",\"chainId\":\"0x1\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"},{\"hash\":\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\",\"nonce\":\"0x2\",\"blockHash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"blockNumber\":\"0x3\",\"blockTimestamp\":\"0x5e47e919\",\"transactionIndex\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x1\",\"gasPrice\":\"0x1\",\"gas\":\"0x5208\",\"input\":\"0x\",\"chainId\":\"0x1\",\"type\":\"0x0\",\"v\":\"0x25\",\"s\":\"0x575361bb330bf38b9a89dd8279d42a20d34edeaeede9739a7c2bdcbe3242d7bb\",\"r\":\"0xe7c5ff3cba254c4fe8f9f12c3f202150bb9a0aebeee349ff2f4acb23585f56bd\"}],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
    [TestCase(false, "0x0", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, "earliest", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x0\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, "latest", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x7a1200\",\"gasUsed\":\"0x0\",\"hash\":\"0x16b111d85efa64c1c8e27f1e59c8ccd6bb6643b1999628ac37294c31158e2245\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x761cfe357802c8a2a68e37ad8325607920e72ce19b5b0d3e1ba01840f7e905ec\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x20b\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"baseFeePerGas\":\"0x2da282a8\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(true, "0x0", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"baseFeePerGas\":\"0x0\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase(false, "0x20", "{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}")]
    public async Task Eth_get_block_by_number(bool eip1559, string blockParameter, string expectedResult)
    {
        using Context ctx = eip1559 ? await Context.CreateWithLondonEnabled() : await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockByNumber", blockParameter, "true");
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expectedResult)).Using(JToken.EqualityComparer));
    }

    [TestCase("earliest", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase("latest", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":\"0x29f141925d2d8e357ae5b6040c97aa12d7ac6dfcbe2b20e7b616d8907ac8e1f3\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x3\",\"parentHash\":\"0x49e7d7466be0927347ff2f654c014a768b5a5fcd8c483635210466dd0d6d204c\",\"receiptsRoot\":\"0xd95b673818fa493deec414e01e610d97ee287c9421c8eff4102b1647c1a184e4\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x4e786afc8bed76b7299973ca70022b367cbb94c14ec30e9e7273b31b6b968de9\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\"],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
    [TestCase("pending", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0x1\",\"extraData\":\"0x4e65746865726d696e64\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0xa410\",\"hash\":null,\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":null,\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":null,\"number\":\"0x3\",\"parentHash\":\"0x49e7d7466be0927347ff2f654c014a768b5a5fcd8c483635210466dd0d6d204c\",\"receiptsRoot\":\"0xd95b673818fa493deec414e01e610d97ee287c9421c8eff4102b1647c1a184e4\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x2cb\",\"stateRoot\":\"0x4e786afc8bed76b7299973ca70022b367cbb94c14ec30e9e7273b31b6b968de9\",\"totalDifficulty\":\"0xf4243\",\"timestamp\":\"0x5e47e919\",\"transactions\":[\"0x681c2b6f99e37fd6fe6046db8b51ec3460d699cacd6a376143fd5842ac50621f\",\"0x7126cf20a0ad8bd51634837d9049615c34c1bff5e1a54e5663f7e23109bff48b\"],\"transactionsRoot\":\"0x2e6e6deb19d24bd48eda6071ab38b1bae64c15ef1998c96f0d153711d3a3efc7\",\"uncles\":[]},\"id\":67}")]
    [TestCase("0x0", "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x201\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"totalDifficulty\":\"0xf4240\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]},\"id\":67}")]
    [TestCase("0x20", "{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}")]
    public async Task Eth_get_block_by_number_no_details(string blockParameter, string expectedResult)
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockByNumber", blockParameter, "false");
        Assert.That(serialized, Is.EqualTo(expectedResult), serialized.Replace("\"", "\\\""));

        string serialized2 = await ctx.Test.TestEthRpc("eth_getBlockByNumber", blockParameter);
        Assert.That(serialized2, Is.EqualTo(expectedResult), serialized2);
    }

    [TestCase("hash")]
    [TestCase("nonce")]
    [TestCase("miner")]
    public async Task Eth_getBlockByNumber_pending_fields_should_be_null(string field)
    {
        using Context ctx = await Context.Create();

        string serialized = await ctx.Test.TestEthRpc("eth_getBlockByNumber", "pending", "true");
        JToken json = JToken.Parse(serialized);

        Assert.That(json["result"]![field]!.Type, Is.EqualTo(JTokenType.Null));
    }

    [TestCase("0x0")]
    public async Task Eth_get_block_by_number_should_not_recover_tx_senders_for_request_without_tx_details(string blockParameter)
    {
        IBlockchainBridge? blockchainBridge = Substitute.For<IBlockchainBridge>();
        TestRpcBlockchain ctx = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(blockchainBridge).Build(MainnetSpecProvider.Instance);
        await ctx.TestEthRpc("eth_getBlockByNumber", blockParameter, "false");
        blockchainBridge.Received(0).RecoverTxSenders(Arg.Any<Block>());
    }


    [Test]
    public async Task Eth_get_block_by_number_null()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockByNumber", 1000000.ToHexString(), "false");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}"));
    }

    [TestCase(true, TestName = "ByHashGenesis")]
    [TestCase(false, TestName = "ByNumberLatest")]
    public async Task EthGetHeaderByX_WhenBlockExists_OmitsBodyFields(bool byHash)
    {
        using Context ctx = await Context.Create();
        string method = byHash ? "eth_getHeaderByHash" : "eth_getHeaderByNumber";
        string param = byHash ? ctx.Test.BlockTree.Genesis!.Hash!.ToString() : "latest";
        string serialized = await ctx.Test.TestEthRpc(method, param);
        JObject result = (JObject)JToken.Parse(serialized)["result"]!;

        // Body-level fields must NOT appear in header response (Geth's RPCMarshalHeader excludes them).
        Assert.That(result.ContainsKey("size"), Is.False);
        Assert.That(result.ContainsKey("transactions"), Is.False);
        Assert.That(result.ContainsKey("uncles"), Is.False);
        Assert.That(result.ContainsKey("totalDifficulty"), Is.False);

        // Core header fields must be present.
        foreach (string field in new[] { "number", "hash", "parentHash", "nonce", "stateRoot", "transactionsRoot", "receiptsRoot", "logsBloom" })
        {
            Assert.That(result.ContainsKey(field), Is.True, $"header response must include '{field}'");
        }
    }

    [TestCase("eth_getHeaderByHash", "0x0000000000000000000000000000000000000000000000000000000000000000", TestName = "UnknownHash")]
    [TestCase("eth_getHeaderByNumber", "0x9999999", TestName = "UnknownNumber")]
    [TestCase("eth_getHeaderByNumber", "finalized", TestName = "FinalizedAbsent")]
    [TestCase("eth_getHeaderByNumber", "safe", TestName = "SafeAbsent")]
    public async Task EthGetHeaderByX_WhenBlockUnknown_ReturnsNull(string method, string blockParam)
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc(method, blockParam);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}"));
    }

    [TestCase("hash")]
    [TestCase("nonce")]
    [TestCase("miner")]
    public async Task EthGetHeaderByNumber_WhenPending_NilsTransientFields(string field)
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getHeaderByNumber", "pending");
        JToken json = JToken.Parse(serialized);
        Assert.That(json["result"]![field]!.Type, Is.EqualTo(JTokenType.Null));
    }

    [Test]
    public async Task EthGetHeaderByHash_WhenAuraBlock_EmitsAuraFields()
    {
        using Context ctx = await Context.Create();
        TestRpcBlockchain aura = ctx.AuraTest;
        string serialized = await aura.TestEthRpc("eth_getHeaderByHash", aura.BlockTree.Genesis!.Hash!.ToString());
        JObject result = (JObject)JToken.Parse(serialized)["result"]!;

        // AuRa-specific fields must be present (proves the AuRa branch fired).
        Assert.That(result.ContainsKey("signature"), Is.True);
        Assert.That(result.ContainsKey("step"), Is.True);

        // PoW nonce is declared with [JsonIgnoreCondition.Never] so it serializes as null on AuRa headers.
        Assert.That(result["nonce"]!.Type, Is.EqualTo(JTokenType.Null));

        // PoW mixHash uses default null-omit semantics — absent or null, not a non-null value.
        JToken? mixHashToken = result["mixHash"];
        Assert.That((mixHashToken is null || mixHashToken.Type == JTokenType.Null), Is.True);
    }

    [Test]
    public async Task EthGetHeaderByNumber_WhenEarliest_MatchesExpectedJson()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getHeaderByNumber", "earliest");
        // Same value set as the genesis row in Eth_get_block_by_number "earliest", minus
        // size/totalDifficulty/transactions/uncles (header endpoint excludes body-level fields).
        const string expected = "{\"jsonrpc\":\"2.0\",\"result\":{\"difficulty\":\"0xf4240\",\"extraData\":\"0x010203\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2167088a0f0de66028d2b728235af6d467108c1750c3e11a8f6e6cd60fddb0e4\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"stateRoot\":\"0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f\",\"timestamp\":\"0xf4240\",\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\"},\"id\":67}";
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task Eth_protocol_version()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_protocolVersion");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0x44\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_code()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getCode", TestItem.AddressA.ToString(), "latest");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0xabcd\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_code_default()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getCode", TestItem.AddressA.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":\"0xabcd\",\"id\":67}"));
    }

    [Test]
    public async Task Eth_accounts()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_accounts");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":[\"0x7e5f4552091a69125d5dfcb7b8c2659029395bdf\",\"0x2b5ad5c4795c026514f8317c7a215e218dccd6cf\",\"0x6813eb9362372eef6200f3b1dbc3f819671cba69\",\"0x1eff47bc3a10a45d4b230b5d10e37751fe6aa718\",\"0xe1ab8145f7e55dc933d51a18c793f901a3a0b276\",\"0xe57bfe9f44b819898f47bf37e5af72a0783e1141\",\"0xd41c057fd1c78805aac12b0a94a405c0461a6fbb\",\"0xf1f6619b38a98d6de0800f1defc0a6399eb6d30c\",\"0xf7edc8fa1ecc32967f827c9043fcae6ba73afa5c\",\"0x4cceba2d7d2b4fdce4304d3e09a1fea9fbeb1528\",\"0x3da8d322cb2435da26e9c9fee670f9fb7fe74e49\",\"0xdbc23ae43a150ff8884b02cea117b22d1c3b9796\",\"0x68e527780872cda0216ba0d8fbd58b67a5d5e351\",\"0x5a83529ff76ac5723a87008c4d9b436ad4ca7d28\",\"0x8735015837bd10e05d9cf5ea43a2486bf4be156f\",\"0xfae394561e33e242c551d15d4625309ea4c0b97f\",\"0x252dae0a4b9d9b80f504f6418acd2d364c0c59cd\",\"0x79196b90d1e952c5a43d4847caa08d50b967c34a\",\"0x4bd1280852cadb002734647305afc1db7ddd6acb\",\"0x811da72aca31e56f770fc33df0e45fd08720e157\",\"0x157bfbecd023fd6384dad2bded5dad7e27bf92e4\",\"0x37da28c050e3c0a1c0ac3be97913ec038783da4c\",\"0x3bc8287f1d872df4217283b7920d363f13cf39d8\",\"0xf4e2b0fcbd0dc4b326d8a52b718a7bb43bdbd072\",\"0x9a5279029e9a2d6e787c5a09cb068ab3d45e209d\",\"0xc39677f5f47d5fe65ab24e66750e8fca127c15be\",\"0x1dc728786e09f862e39be1f39dd218ee37feb68d\",\"0x636cc65783084b9f370789c90f733dbbeb88925d\",\"0x4a7a7c2e09209dbe44a582cd92b0edd7129e74be\",\"0xa56160a359f2eaa66f5c9df5245542b07339a9a6\",\"0x6b09d6433a379752157fd1a9e537c5cae5fa3168\",\"0x32e77de0d74a5c7af861aaed324c6a4c488142a8\",\"0x093d49d617a10f26915553255ec3fee532d2c12f\",\"0x138854708d8b603c9b7d4d6e55b6d32d40557f4d\",\"0x7dc0a40d64d72bb4590652b8f5c687bf7f26400c\",\"0x9358a525cc25aa571af0bcb5b98fbeab045a5e36\",\"0xd8e8ea89d71de89214fa39ba13ba9fcddc0d9467\",\"0xb56ed8f48979e1a948ad129199a600d0562cac51\",\"0xf65ac7003e905d72c666bfec1dc0960ecc9d0d6e\",\"0xd817d23c981472d703be36da777ffdb1abefd972\",\"0xf2adb90aa27a3c61a95c50063b20919d811e1476\",\"0xae3dffee97f92db0201d11cb8877c89738353bce\",\"0xeb3025e7ac2764040384316b33476e048961a71f\",\"0x9e3289708dc5709926a542fcf260fd4b210461f0\",\"0x6c23face014f20b3ebb65ae96d0d7ff32ab94c17\",\"0xb83b6241f966b1685c8b2ffce3956e21f35b4dcb\",\"0x6350872d7465864689def650443026f2f73a08da\",\"0x673c638147fe91e4277646d86d5ae82f775eea5c\",\"0xf472086186382fca55cd182de196520abd76f69d\",\"0x5ae58d2bc5145bff0c1bec0f32bfc2d079bc66ed\",\"0x2b29bea668b044b2b355c370f85b729bcb43ec40\",\"0x3797126345fb5fb6a37629db55ec692173cfb458\",\"0xe6869cc98283ab53e8a1a5312857ef0be9d189fe\",\"0xa5dfe354b3fc30c5c3a8ffefc8f9470d9177c334\",\"0xa1a625ae13b80a9c48b7c0331c83bc4541ac137f\",\"0xa33c9d26e1e33b84247defca631c1d30ffc76f5d\",\"0xf9807a8719ae154e74591cb2d452797707fadf73\",\"0xa1ba6fc3ea0e89f0e79f89d9aa0081d010571e4a\",\"0x366c20b40048556e5682e360997537c3715aca0e\",\"0xeb0e56f32246d043228fac8b63a71687d5199af1\",\"0xdb3ed822b78f0641623a12166607b5fa4df862ad\",\"0xb88c19426f03c6981d1a4281c7414d842b97619a\",\"0x32e04b012ac811c91d36a355a6d2859a0071a965\",\"0xe0dd44773f7657b11019062879d65f3d9862460c\",\"0x756be12856a8f44ab22fdbcbd42b70b843377d09\",\"0x6f4c950442e1af093bcff730381e63ae9171b87a\",\"0x4d1bf28514a4451249908e611366ec967c3d1558\",\"0xb0142d883494197b02c6ece84f571d81bd831124\",\"0x1326324f5a9fb193409e10006e4ea41b970df321\",\"0xf9a2c330a19e2fbfeb50fe7a7195b973bb0a3be9\",\"0x7a601ffa997cede6435aeabf4fa2091f09e149ec\",\"0xa92f4b5c4fddcc37e5139873ac28a4a0a42d68df\",\"0x850cc185d6cae4a7fdfb3dd81f977dd1df7d6503\",\"0xb1b7c87e8a0bf2e7fd1a1c582bd353e4c4529341\",\"0xff844fdb49e00776ad538db9ea2f9fa98ec0caf7\",\"0x1ac6f9601f2f616badcea8a0a307e1a3c14767a4\",\"0xc2aa6271409c10dee630e79df90c968989ccf2b7\",\"0x883d01eae6eaac077e126ddb32cd53550966ed76\",\"0x127688bbc070dd69a4db8c3ba5d43909e13d8f77\",\"0x0b54a50c0409dab2e63c3566324268ed53ec019a\",\"0xafd46e3549cc63d7a240d6177d056857679e6f99\",\"0x752481f35bb1d44d786c7b4dbe40db4a4266f96f\",\"0xac32def421e36b43629f785fd04523260e7f2b28\",\"0xfe6032a0810e90d025a3a39dd29844f964ee102c\",\"0x5cb6f3e6499d1f068b33351d0cae4b68cdf501bf\",\"0x84b743441b7bdf65cb4293126db4c1b709d7d95e\",\"0x8530a26f6c062f55597bd30c1a44e248decb0027\",\"0x5ce162cfa6208d7c50a7cb3525ac126155e7bce4\",\"0x2853dc9ca40d012969e25360cce0d9d326b24a86\",\"0x802271c02f76701929e1ea772e72783d28e4b60f\",\"0x7bd2aa0726ac3b9e752b120de8568e90b0423ae4\",\"0xb540c05d9b2516da9596a5ee75d750717a4be035\",\"0xa72392cd4be285ab6681da1bf1c45f0b370cb7b4\",\"0xcf484269182ac2388a4bfe6d19fb0687e3534b7f\",\"0x994907cb80bfd175f9b0b32672cfde0091368e2e\",\"0x36eab6ce7fededc098ef98c41e83548a89147131\",\"0x440db3ab842910d2a39f4e1be9e017c6823fb658\",\"0x25ac70ea6f44c4531a7117ea3620fa29cdaaca48\",\"0x24d881139ee639c2a774b4b1851cb7a9d0fce122\",\"0xd9a284367b6d3e25a91c91b5a430af2593886eb9\",\"0xe6b3367318c5e11a6eed3cd0d850ec06a02e9b90\",\"0x88c0e901bd1fd1a77bda342f0d2210fdc71cef6b\",\"0x7231c364597f3bfdb72cf52b197cc59111e71794\",\"0x043aed06383f290ee28fa02794ec7215ca099683\",\"0x0c95931d95694b3ef74071241827c09f25d40620\",\"0x417f3b59ef57c641283c2300fae0f27fe98d518c\",\"0xd6b931d8d441b1ec98f55f8ec8adb532dc140c78\",\"0x9220625b1a30680387d542e6b5f753786ca5530e\",\"0x997cf669860a1dcc76344866534d8679a7b562e2\",\"0xb961768b578514debf079017ff78c47b0a6adbf6\",\"0x052b91ad9732d1bce0ddae15a4545e5c65d02443\",\"0x8df64de79608f0ae9e72ecae3a400582aed8101c\",\"0x0e7b23cd1fdb7ea3ccc80320ab43843a2f193c36\",\"0xfbbc41289f834a76e4320ac238512035560467ee\",\"0x61e1da6c7b8b211e6e5dd921efe27e73ad226dac\",\"0x87fcbe64187317c59a944be5b9c5c830b9373730\",\"0x2acf0d6fdac920081a446868b2f09a8dac797448\",\"0x1715eb68afba4d516ef1e068b55f5093bb4a2f59\",\"0x58bab2f728dc4fc227a4c38cab2ec93b73b4e828\",\"0x25346934b4faa00dee0190c2069156bde6010c18\",\"0xa01cca6367a84304b6607b76676c66c360b74741\",\"0x872917cec8992487651ee633dba73bd3a9dca309\",\"0x6c1a01c2ab554930a937b0a2e8105fb47946c679\",\"0x13c0e7c715fdea35c7f9663c719e4d36601275b9\",\"0xe8c5025c803f3279d94ea55598e147f601929bf4\",\"0x639acdbd838b81cea8d6a970136812783fa5bf5e\",\"0xb3087f34edab33a8182ba29adea4d739d9831a94\",\"0xc6a210606f2ee6e64afb9584db054f3476a5cc66\",\"0xd01c9d93efc83c00b30f768f832182beff65696f\",\"0x00edf2d16afbc028fb1e879559b07997af79539f\",\"0xf5d722030d17ca01f2813af9e7be158d7a037997\",\"0xae3d43ab6fdcd35386db427099ff11aa670ee0f4\",\"0x0dc8b8ef8457b1e45ac277d65ac5987b547ba775\",\"0xde521346f9327a4314a18b2cda96b2a33603177a\",\"0x69842e12d6f36f9f93f06086b70795bfc7e02745\",\"0x9b7bdf6ad17d5fc9a168acaa24495e52a65f3b79\",\"0xa2d47d2c42009520075cb15f5855052008d0c44d\",\"0xb0c249f6f92fb2491fc9750a5299d856ba2ea3c6\",\"0x839d96957f21e82fbcca0d42a1f12ef6e1cf82e9\",\"0x2a0d6b92b042497013e5549d6579202608ce0c80\",\"0xa4f8c598927eab2f1898f8f2d6f8121578de2344\",\"0xdb21655b672dacc8da6f538c899f9d6969604117\",\"0x21289cd01f9f58fc44962b6e213a0fbbd015beb6\",\"0x0b62d63c314d94dfa85b11a9c652ffe438382d6c\",\"0x9383e3096133f464d516b518b12851fd10d891f4\",\"0x64e582c17ab7c3b90e171795b504ca3c04108501\",\"0x848406919d014b1e5c27a82f951caff840fd63ef\",\"0x5fe015779fb36006b01f9c5a5dbcaa6ffa56f0c0\",\"0x28b6e15f86025b8ea8beaa6855a81069bfb6ab1e\",\"0x271d65af9a5a7b4cd7af264f251184c2a4b9e7a3\",\"0xddf44e34ed40c40624c7b9f20a1030b505a4fac0\",\"0xe5854075272ca5ef71663d5b87e0cd5ac53b2f36\",\"0x2798ba84d7830c5f60d750f37f87d93277106905\",\"0x7e9961fa09dd52f945f8143844785cf0e51bb4ce\",\"0xf33d2f7d96f92d912ca8418f9d62eb54c1a9889f\",\"0xeec566c793a89f388bbabfc0225183a6a95c4263\",\"0x2001f8cdcdeef1bbcc188ca59cf04fb44133d55a\",\"0x3bf958fa0626e898f548a8f95cf9ab3a4db65169\",\"0xb0d744fde06bbcb6655eb55288ec94fa6a0b2a52\",\"0x18eb36d090eeadf82f3454a6da690fc398d3eba1\",\"0xd2431ca38735c2fd438e2caa23f094191d89675b\",\"0x612b7be154a64292aae070aaa86fcd66ba218071\",\"0x681ce2f439fdc80e70c1eea8b8a085dfb976d32a\",\"0x2174ca3ee9ace7dd8c946c97054c72f2b384c4c2\",\"0x1d694d5ad94f32132ff5c14c901d3ddbee90a550\",\"0x0b6fe046e6fd8d7a7a36d5ad1ffb82d2e3e5c3bb\",\"0x258f4ed0560e290a95066d9dee3628f2f179302b\",\"0xe2a09565167d4e3f826adec6bef82b97e0a4383f\",\"0x9af70704e9ec5f505cdba564ff4dec03503ddaa2\",\"0xeb9afe072c781401bf364224c75a036e4d832f52\",\"0x07748403082b29a45abd6c124a37e6b14e6b1803\",\"0x63486b70d804464766cfd096bba5552c4bcdac30\",\"0x5181be40152caaba8e123a55b7762755d4e8e416\",\"0x9481da7766c043eefeecc9589ee7ade61316b0ff\",\"0x42aba3530dd1ccb1dda27bfaa7c6a832cfdb4446\",\"0x05650444ace15a01762bd97ee8fdeb495b3c2436\",\"0xd83d18a2eae2440e272a53f86e617cd9f33c8d68\",\"0x4a35a802dbd623561040dd50f6293842d0901731\",\"0x4dbaf6c348d8cd1f174a7a6155f80ea8d4a8baf8\",\"0x9efc4e49be8ff70d596ac20efec9b7842e1ea963\",\"0x68efde0cdd917c6da6dab02c23f69e7c9cff51a9\",\"0x99b52813933a46d95bd4265ea2f674e58827da97\",\"0x7b35461cc5adbdc415c1f9562ccc342adbf09bd4\",\"0x8ee8813fb9d41cc58ef87d28b36e948b1234e71d\",\"0x69c1bc7883a7bb7696c7726d025867cd16564c9c\",\"0x31eb18dd6f5a8064ab750eabb281cf162f43ccd0\",\"0xf5d122e123d9d7998d2bea685d11b10fec3e4508\",\"0xf762854586a40a93d1fdcde32c062829f3754de9\",\"0x1e3f8fb9f840325983d6e5c68b6b846ff66a20ac\",\"0x3c1638a25ad7e8c2a84b53b661dd1bd048407e8f\",\"0x2eedf8a799b73bc02e4664183eb72422c377153b\",\"0xcdef6f23a26f960b53468f497967ce74c026af52\",\"0x0a2035683fe5587b0b09db0e757306ad729fe6c1\",\"0x158cc083cfbcffb2f983a3aa8b027eb0711c9831\",\"0x691cb1645a4f21d879973b3a3b98a714fc1970d6\",\"0x754164c0cb85dda1b5b18e5b62adbb4d60c3efbf\",\"0x556330e8d92912ccf133851ba03abd2db70da404\",\"0x1745ceba112b0a41638e235ec59b35adf37b70ab\",\"0xa24c85b16a440587793f82e358fa6b204468735b\",\"0x5304fb08724d73f2bb5e04c582407c33cde6c8d3\",\"0x256a11785fc43141324cf61efb5f491378c10c85\",\"0xa9f161a2badd44f3fe45b91a044a9484b72f1dc4\",\"0xd5cc10c45fc0f9f956acd7559f61edbfec9f6c3d\",\"0x381c7a71035bdb42fb5d77523df2ff00d9f9df1b\",\"0x45cdbeea730d8212f451a6a8d0eb5998b04cccca\",\"0x6367283f25a32be0c28623d787c319e237c3b7bd\",\"0x598e94eb5e050045272d8417f6ab363bd874d568\",\"0x379ff6375f4a44f458683629ef05141d73fae55a\",\"0x18df8ba2ef19083ddff68f8b33976cf22e8419c6\",\"0xffceebc37a7351d5df9aa3b077ed39cc3b5192ff\",\"0x1cd21f00b58894260f7abed65ad23dce3cea0226\",\"0x26324733d604abb6176cf18e4f4a0624ceeddc09\",\"0x4102d394d723ff141b82ef9a6053fb89f90c67f4\",\"0x269c370cb95b63f9b6a7cad47998167f160a2689\",\"0x3bb9557113fbb052dae3008a2801a072c432c018\",\"0x3f588a72d94d0d0986b112c671c2343320a19386\",\"0x7cfa9eee1d752da599211bc8a68d0687708dabfc\",\"0x7bc23966c419eadcb8a2fc5f83e635c4d4ad0c2f\",\"0xc4ad60337b04fc721912531a52a5d77878293fb9\",\"0xfc5ba3041f750f9b6820ce066c153eb396aac1ff\",\"0x32480c2d857941d2fff4e34f0910b20c0f9c23bc\",\"0x8041c9a96585053db2d7214b5de56828645b8e62\",\"0x444ca66b3ceb4187840cb1a205566a1413d5fecb\",\"0xf084bbaabee1a700a8faa404027db620a5aa0059\",\"0x602d562b4ef2544f851587619b56f77a9d965d45\",\"0x216faec139a61329ef8b31d982de427d9c007a9e\",\"0x11eb17b20113ae923d72e52870d40bf59a08b40d\",\"0xe69017fcc36bbc7fb167b9585bdd47a950ba1992\",\"0xe5549f429a72bfa618cf5c1afdac22a730df6a1a\",\"0x161c2e10407e2a87959c0bae1f342c80eaea28f3\",\"0x4161220db043a7d682e0ad123a3f8fea165711aa\",\"0xb33609811fb3d9fc8955dc6e9e086f1f08fc3a65\",\"0x4148555ea4c00e14f81ef399bbe67ef2fd9811b1\",\"0x4f81e991f76276a17ca92a1321f37189b1727f77\",\"0xba95e317ead06b55c8b70276fc63904b3339dfa1\",\"0xf6203c4fb14da640d11fbd9573e3958d017e6745\",\"0x73377d6228266393747efa710017872d6dd5b9a6\",\"0xf7862d105fc6ee69604decc30aa89472ad405961\",\"0xfa1205e19719c248323563bd55cd8bfd08b0cbc6\",\"0x4f46630115b446f8f7cebe1e5961ef7858c25204\",\"0x7492ebbc1e7f2838fc7191edc031581d5712978a\",\"0xc0af3981f9c0dfcb8955fea07a3e4f23806fab51\",\"0x8621dd642245df371b584b48c081e8863313a70d\",\"0xc328de035c91b39efa07d2fe620813253c9b4ec2\",\"0xa11308e3b741227d41973ddb17534ceb27b8206f\",\"0xc4ff1b4565ee203fa12636e100fe9c89cd18acb7\",\"0x63a36aea8570219476ef835f09024acdedfee95a\",\"0xf7205066c153f7c88dc3653ebc72b438884ae109\",\"0xa8ce5c40c4aa9278ddeaa418e775985549960e7a\",\"0x81f58f2194b0413806bf2ce8e1654bc855dc65a1\",\"0xf0218008120201e66b65fce4df9035007e811228\",\"0x90f022e3ca8453f5e5765cd3054003b544526eec\",\"0x1d1f873ba1ddf7915e6e26f93f5624b40efaefe2\",\"0x0311afd3bc2ae250d5f9f2706bae2ef4164d6912\",\"0x5044a80bd3eff58302e638018534bbda8896c48a\"],\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_block_by_number_with_number_bad_number()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockByNumber", "'0x1234567890123456789012345678901234567890123456789012345678901234567890'", "true");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_proof()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getProof", TestBlockchain.AccountA.ToString(), "[]", "0x2");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"accountProof\":[\"0xf8718080808080a0fc8311b2cabe1a1b33ea04f1865132a44aa0c17c567acd233422f9cfb516877480808080a0be8ea164b2fb1567e2505295dae6d8a9fe5f09e9c5ac854a7da23b2bc5f8523ca053692ab7cdc9bb02a28b1f45afe7be86cb27041ea98586e6ff05d98c9b0667138080808080\",\"0xf8518080808080a00dd1727b2abb59c0a6ac75c01176a9d1a276b0049d5fe32da3e1551096549e258080808080808080a038ca33d3070331da1ccf804819da57fcfc83358cadbef1d8bde89e1a346de5098080\",\"0xf872a020227dead52ea912e013e7641ccd6b3b174498e55066b0c174a09c8c3cc4bf5eb84ff84d01893635c9adc5de9fadf7a0475ae75f323761db271e75cbdae41aede237e48bc04127fb6611f0f33298f72ba0dbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322\"],\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"balance\":\"0x3635c9adc5de9fadf7\",\"codeHash\":\"0xdbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322\",\"nonce\":\"0x1\",\"storageHash\":\"0x475ae75f323761db271e75cbdae41aede237e48bc04127fb6611f0f33298f72b\",\"storageProof\":[]},\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_proof_withTrimmedAndDuplicatedStorageKey()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getProof", TestBlockchain.AccountA.ToString(), "[\"0x1\"]", "0x2");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"accountProof\":[\"0xf8718080808080a0fc8311b2cabe1a1b33ea04f1865132a44aa0c17c567acd233422f9cfb516877480808080a0be8ea164b2fb1567e2505295dae6d8a9fe5f09e9c5ac854a7da23b2bc5f8523ca053692ab7cdc9bb02a28b1f45afe7be86cb27041ea98586e6ff05d98c9b0667138080808080\",\"0xf8518080808080a00dd1727b2abb59c0a6ac75c01176a9d1a276b0049d5fe32da3e1551096549e258080808080808080a038ca33d3070331da1ccf804819da57fcfc83358cadbef1d8bde89e1a346de5098080\",\"0xf872a020227dead52ea912e013e7641ccd6b3b174498e55066b0c174a09c8c3cc4bf5eb84ff84d01893635c9adc5de9fadf7a0475ae75f323761db271e75cbdae41aede237e48bc04127fb6611f0f33298f72ba0dbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322\"],\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"balance\":\"0x3635c9adc5de9fadf7\",\"codeHash\":\"0xdbe576b4818846aa77e82f4ed5fa78f92766b141f282d36703886d196df39322\",\"nonce\":\"0x1\",\"storageHash\":\"0x475ae75f323761db271e75cbdae41aede237e48bc04127fb6611f0f33298f72b\",\"storageProof\":[{\"key\":\"0x1\",\"proof\":[\"0xe7a120b10e2d527612073b26eecdfd717e6a320cf44b4afac2b0732d9fcbe2b7fa0cf68483abcdef\"],\"value\":\"0xabcdef\"}]},\"id\":67}"), serialized.Replace("\"", "\\\""));
    }

    [Test]
    public async Task Eth_get_proof_withTooManyKeys()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getProof", TestBlockchain.AccountA.ToString(), $"[{string.Join(", ", Enumerable.Range(1, EthRpcModule.GetProofStorageKeyLimit + 1).Select(i => "\"" + i.ToHexString() + "\""))}]", "0x2");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"storageKeys: 1001 is over the query limit 1000.\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_proof_for_non_existent_account_returns_zero_hashes()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getProof", "0x000000000000000000000000000000000000dead", "[]", "0x2");

        JObject result = (JObject)JToken.Parse(serialized)["result"]!;
        Assert.Multiple(() =>
        {
            Assert.That((string?)result["address"], Is.EqualTo("0x000000000000000000000000000000000000dead"));
            Assert.That((string?)result["balance"], Is.EqualTo("0x0"));
            Assert.That((string?)result["nonce"], Is.EqualTo("0x0"));
            Assert.That((string?)result["codeHash"], Is.EqualTo(Hash256.Zero.ToString()));
            Assert.That((string?)result["storageHash"], Is.EqualTo(Hash256.Zero.ToString()));
        });
    }

    [Test]
    public async Task Eth_get_block_by_number_empty_param()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockByNumber", "", "true");
        Assert.That(serialized.StartsWith("{\"jsonrpc\":\"2.0\",\"error\""), Is.True);
    }

    [Test]
    public async Task Eth_get_account_notfound()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getAccount", "0x000000000000000000000000000000000000dead", "latest");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_account_found()
    {
        using Context ctx = await Context.Create();
        string account_address = TestBlockchain.AccountC.ToString();

        string serialized = await ctx.Test.TestEthRpc("eth_getAccount", account_address, "latest");
        string expected = "{\"jsonrpc\":\"2.0\",\"result\":{\"codeHash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"storageRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"balance\":\"0x3635c9adc5dea00000\",\"nonce\":\"0x0\"},\"id\":67}";

        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public async Task Eth_get_account_incorrect_block()
    {
        using Context ctx = await Context.Create();
        string account_address = TestBlockchain.AccountC.ToString();

        string serialized = await ctx.Test.TestEthRpc("eth_getAccount", account_address, "0xffff");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"header not found\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_account_no_block_argument()
    {
        using Context ctx = await Context.Create();
        string account_address = TestBlockchain.AccountC.ToString();

        string serialized = await ctx.Test.TestEthRpc("eth_getAccount", account_address);
        string expected = "{\"jsonrpc\":\"2.0\",\"result\":{\"codeHash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"storageRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"balance\":\"0x3635c9adc5dea00000\",\"nonce\":\"0x0\"},\"id\":67}";

        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public async Task Eth_get_account_info_notfound()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_getAccountInfo", "0x000000000000000000000000000000000000dead", "latest");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"code\":\"0x\",\"balance\":\"0x0\",\"nonce\":\"0x0\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_account_info_found()
    {
        using Context ctx = await Context.Create();
        string account_address = TestBlockchain.AccountC.ToString();

        string serialized = await ctx.Test.TestEthRpc("eth_getAccountInfo", account_address, "latest");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"code\":\"0x\",\"balance\":\"0x3635c9adc5dea00000\",\"nonce\":\"0x0\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_account_info_incorrect_block()
    {
        using Context ctx = await Context.Create();
        string account_address = TestBlockchain.AccountC.ToString();

        string serialized = await ctx.Test.TestEthRpc("eth_getAccountInfo", account_address, "0xffff");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"header not found\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_account_info_no_block_argument()
    {
        using Context ctx = await Context.Create();
        string account_address = TestBlockchain.AccountC.ToString();

        string serialized = await ctx.Test.TestEthRpc("eth_getAccountInfo", account_address);

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"code\":\"0x\",\"balance\":\"0x3635c9adc5dea00000\",\"nonce\":\"0x0\"},\"id\":67}"));
    }


    [Test]
    public async Task Eth_get_block_by_number_with_recovering_sender_from_receipts()
    {
        using Context ctx = await Context.Create();
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();

        Block block = Build.A.Block.WithNumber(1)
            .WithStateRoot(new Hash256("0xe4a589578a2838164c89c02e38528d7865b132981a9793a8f0b443fcfe70728f"))
            .WithTransactions(Build.A.Transaction.TestObject)
            .TestObject;

        LogEntry[] entries = {
            Build.A.LogEntry.TestObject,
            Build.A.LogEntry.TestObject
        };

        TxReceipt receipt = Build.A.Receipt.WithBloom(new Bloom(entries, new Bloom())).WithAllFieldsFilled
            .WithSender(TestItem.AddressE)
            .WithLogs(entries).TestObject;
        TxReceipt[] receiptsTab = { receipt };
        blockFinder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
        receiptFinder.Get(Arg.Any<Block>()).Returns(receiptsTab);
        receiptFinder.Get(Arg.Any<Hash256>()).Returns(receiptsTab);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockFinder).WithReceiptFinder(receiptFinder).Build();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockByNumber", TestItem.KeccakA.ToString(), "true");

        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse("""{"jsonrpc":"2.0","result":{"difficulty":"0xf4240","extraData":"0x010203","gasLimit":"0x3d0900","gasUsed":"0x0","hash":"0xd0b838318d6d90b04addc0ba3600a2c21ce390f0f2eacc73eb88c37c23df20fb","logsBloom":"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000","miner":"0x0000000000000000000000000000000000000000","mixHash":"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2","nonce":"0x00000000000003e8","number":"0x1","parentHash":"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c","receiptsRoot":"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421","sha3Uncles":"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347","size":"0x221","stateRoot":"0xe4a589578a2838164c89c02e38528d7865b132981a9793a8f0b443fcfe70728f","timestamp":"0xf4240","transactions":[{"nonce":"0x0","blockHash":"0xd0b838318d6d90b04addc0ba3600a2c21ce390f0f2eacc73eb88c37c23df20fb","blockNumber":"0x1","blockTimestamp":"0xf4240","transactionIndex":"0x0","from":"0x2d36e6c27c34ea22620e7b7c45de774599406cf3","to":"0x0000000000000000000000000000000000000000","value":"0x1","gasPrice":"0x1","gas":"0x5208","input":"0x","type":"0x0","v":"0x0","r":"0x0","s":"0x0","hash":null}],"transactionsRoot":"0x29cc403075ed3d1d6af940d577125cc378ee5a26f7746cbaf87f1cf4a38258b5","uncles":[]},"id":67}""")).Using(JToken.EqualityComparer));
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
            .WithTimestamp(10)
            .WithStateRoot(new Hash256("0xe4a589578a2838164c89c02e38528d7865b132981a9793a8f0b443fcfe70728f"))
            .TestObject;

        LogEntry[] entries = new[]
        {
            Build.A.LogEntry.TestObject,
            Build.A.LogEntry.TestObject
        };

        TxReceipt receipt = Build.A.Receipt.WithBloom(new Bloom(entries, new Bloom())).WithAllFieldsFilled
            .WithLogs(entries).TestObject;
        TxReceipt[] receiptsTab = { receipt };


        blockchainBridge.GetTxReceiptInfo(Arg.Any<Hash256>())
            .Returns((receipt, 10, postEip4844 ? new(UInt256.One, 2, 3) : new(UInt256.One), 0));
        blockFinder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
        receiptFinder.Get(Arg.Any<Block>()).Returns(receiptsTab);
        receiptFinder.Get(Arg.Any<Hash256>()).Returns(receiptsTab);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockFinder).WithReceiptFinder(receiptFinder).WithBlockchainBridge(blockchainBridge).Build();
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionReceipt", TestItem.KeccakA.ToString());

        if (postEip4844)
            Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"cumulativeGasUsed\":\"0x3e8\",\"gasUsed\":\"0x64\",\"blobGasUsed\":\"0x3\",\"blobGasPrice\":\"0x2\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x0\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"blockTimestamp\":\"0xa\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x1\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"blockTimestamp\":\"0xa\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"root\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"type\":\"0x0\"},\"id\":67}"));
        else
            Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"cumulativeGasUsed\":\"0x3e8\",\"gasUsed\":\"0x64\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x0\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"blockTimestamp\":\"0xa\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x1\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"blockTimestamp\":\"0xa\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"root\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"type\":\"0x0\"},\"id\":67}"));
    }


    [Test]
    public async Task Eth_get_transaction_receipt_when_block_has_few_receipts()
    {
        using Context ctx = await Context.Create();
        IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();

        ulong blockNumber = 1;
        ulong timestamp = 10;
        Block genesis = Build.A.Block.Genesis
            .WithStateRoot(new Hash256("0xe4a589578a2838164c89c02e38528d7865b132981a9793a8f0b443fcfe70728f"))
            .TestObject;
        Block previousBlock = genesis;
        Block block = Build.A.Block.WithNumber(blockNumber).WithParent(previousBlock).WithTimestamp(timestamp)
            .WithStateRoot(new Hash256("0xe4a589578a2838164c89c02e38528d7865b132981a9793a8f0b443fcfe70728f"))
            .TestObject;

        LogEntry[] logEntries = new[] { Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject };

        TxReceipt receipt1 = new()
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

        TxReceipt receipt2 = new()
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

        blockchainBridge.GetTxReceiptInfo(Arg.Any<Hash256>()).Returns((receipt2, timestamp, new(UInt256.One), 2));

        TxReceipt[] receipts = { receipt1, receipt2 };

        blockFinder.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(blockNumber).TestObject);
        blockFinder.Head.Returns(Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(blockNumber).TestObject).TestObject);
        blockFinder.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
        receiptFinder.Get(Arg.Any<Block>()).Returns(receipts);
        receiptFinder.Get(Arg.Any<Hash256>()).Returns(receipts);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockFinder).WithBlockchainBridge(blockchainBridge).WithReceiptFinder(receiptFinder).Build();
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionReceipt", TestItem.KeccakA.ToString());

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x7d0\",\"gasUsed\":\"0x3e8\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"to\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x2\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"blockTimestamp\":\"0xa\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]},{\"removed\":false,\"logIndex\":\"0x3\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"blockHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockNumber\":\"0x1\",\"blockTimestamp\":\"0xa\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_getTransactionReceipt_return_info_about_mined_tx()
    {
        using Context ctx = await Context.Create();
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();
        IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();

        await ctx.Test.AddFunds(new Address("0x723847c97bc651c7e8c013dbbe65a70712f02ad3"), 1.Ether);
        Transaction tx = Build.A.Transaction.WithData(new byte[] { 0, 1 })
            .SignedAndResolved().WithChainId(TestBlockchainIds.ChainId).WithGasPrice(0).WithValue(0).WithGasLimit(210200).WithGasPrice(20.GWei).TestObject;

        ulong timestamp = 10;
        Block block = Build.A.Block.WithNumber(1).WithTimestamp(timestamp)
            .WithStateRoot(new Hash256("0xe4a589578a2838164c89c02e38528d7865b132981a9793a8f0b443fcfe70728f"))
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
        receiptFinder.Get(Arg.Any<Hash256>()).Returns(receiptsTab);
        blockchainBridge.GetTxReceiptInfo(Arg.Any<Hash256>()).Returns((receipt, timestamp, new(UInt256.One), 0));

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockFinder).WithReceiptFinder(receiptFinder).WithBlockchainBridge(blockchainBridge).Build();
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionReceipt", tx.Hash!.ToString());

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0xda6b4df2595675cbee0d4889f41c3d0790204e8ed1b8ad4cadaa45a7d50dace5\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"cumulativeGasUsed\":\"0x3e8\",\"gasUsed\":\"0x64\",\"effectiveGasPrice\":\"0x1\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"contractAddress\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"logs\":[{\"removed\":false,\"logIndex\":\"0x0\",\"transactionIndex\":\"0x2\",\"transactionHash\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"blockHash\":\"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72\",\"blockNumber\":\"0x2\",\"blockTimestamp\":\"0xa\",\"address\":\"0x0000000000000000000000000000000000000000\",\"data\":\"0x\",\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"]}],\"logsBloom\":\"0x00000000000000000080000000000000000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000800000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000\",\"root\":\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\",\"type\":\"0x0\"},\"id\":67}"));
    }

    [Test]
    [Ignore("This test is flaky on CI. It could be connected with timeouts in block production.")]
    public async Task Eth_getTransactionReceipt_return_info_about_mined_1559tx()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        await ctx.Test.AddFundsAfterLondon((new Address("0x723847c97bc651c7e8c013dbbe65a70712f02ad3"), 1.Ether));
        Transaction tx = Build.A.Transaction.WithData(new byte[] { 0, 1 })
            .SignedAndResolved().WithChainId(TestBlockchainIds.ChainId).WithGasPrice(0).WithValue(0).WithGasLimit(210200)
            .WithType(TxType.EIP1559).WithMaxFeePerGas(20.GWei).WithMaxPriorityFeePerGas(1.GWei).TestObject;
        await ctx.Test.AddBlock(tx);
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionReceipt", tx.Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"transactionHash\":\"0x31501f80bf2ec493c368a519cb8ed6f132f0be26202304bbf1e1728642affb7f\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0x54515a11aa6c392ee2e1071fca3a579bc9a520930ef757dbf9b7d85fe155c691\",\"blockNumber\":\"0x5\",\"cumulativeGasUsed\":\"0x521c\",\"gasUsed\":\"0x521c\",\"effectiveGasPrice\":\"0x5e91eb5d\",\"from\":\"0x723847c97bc651c7e8c013dbbe65a70712f02ad3\",\"to\":\"0x0000000000000000000000000000000000000000\",\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x1\",\"type\":\"0x2\"},\"id\":67}"));
    }

    [Test]
    [Ignore("This test is flaky on CI. It could be connected with timeouts in block production.")]
    public async Task Eth_getTransactionByHash_return_info_about_mined_1559tx()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        await ctx.Test.AddFundsAfterLondon((new Address("0x723847c97bc651c7e8c013dbbe65a70712f02ad3"), 1.Ether));
        Transaction tx = Build.A.Transaction.WithData(new byte[] { 0, 1 })
            .SignedAndResolved().WithChainId(TestBlockchainIds.ChainId).WithGasPrice(0).WithValue(0).WithGasLimit(210200)
            .WithType(TxType.EIP1559).WithMaxFeePerGas(20.GWei).WithMaxPriorityFeePerGas(1.GWei).TestObject;
        await ctx.Test.AddBlock(tx);
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionByHash", tx.Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"hash\":\"0x31501f80bf2ec493c368a519cb8ed6f132f0be26202304bbf1e1728642affb7f\",\"nonce\":\"0x0\",\"blockHash\":\"0x54515a11aa6c392ee2e1071fca3a579bc9a520930ef757dbf9b7d85fe155c691\",\"blockNumber\":\"0x5\",\"transactionIndex\":\"0x0\",\"from\":\"0x723847c97bc651c7e8c013dbbe65a70712f02ad3\",\"to\":\"0x0000000000000000000000000000000000000000\",\"value\":\"0x0\",\"gasPrice\":\"0x5e91eb5d\",\"maxPriorityFeePerGas\":\"0x3b9aca00\",\"maxFeePerGas\":\"0x4a817c800\",\"gas\":\"0x33518\",\"data\":\"0x0001\",\"input\":\"0x0001\",\"chainId\":\"0x1\",\"type\":\"0x2\",\"v\":\"0x0\",\"s\":\"0x6b82095065a599e6b5e52bed0043702baf3411418af679ac483f9fc75a8f6aef\",\"r\":\"0x8654517f7822e7a4e10e79f3f5a4136703c7d1b51d98e47686e201c3c2845f92\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_chain_id()
    {
        using Context ctx = await Context.Create();
        string serialized = await ctx.Test.TestEthRpc("eth_chainId");
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"0x{TestBlockchainIds.ChainId:X}\",\"id\":67}}"));
    }

    [Test]
    public async Task Eth_chain_id_caches_success_response_and_keeps_request_id_dynamic()
    {
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetChainId().Returns(TestBlockchainIds.ChainId);
        using Context ctx = await Context.Create(blockchainBridge: bridge);
        _ = ctx.Test;
        bridge.ClearReceivedCalls();

        string firstSerialized = await ctx.Test.TestEthRpc("eth_chainId");
        using JsonRpcResponse secondResponse = ctx.Test.EthRpcModule.eth_chainId().WithResponseContext("client-id", null);
        string secondSerialized = RpcTest.SerializeResponse(secondResponse);

        Assert.That(firstSerialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"0x{TestBlockchainIds.ChainId:X}\",\"id\":67}}"));
        Assert.That(secondSerialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"0x{TestBlockchainIds.ChainId:X}\",\"id\":\"client-id\"}}"));
        bridge.Received(1).GetChainId();
    }

    [Test]
    public async Task Send_transaction_with_signature_will_not_try_to_sign()
    {
        using Context ctx = await Context.Create();
        ITxSender txSender = Substitute.For<ITxSender>();
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        txSender.SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.PersistentBroadcast).Returns((TestItem.KeccakA, AcceptTxResult.Accepted));

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockchainBridge(bridge).WithTxSender(txSender).Build();
        Transaction tx = Build.A.Transaction.Signed(new EthereumEcdsa(TestBlockchainIds.ChainId), TestItem.PrivateKeyA).TestObject;
        string serialized = await ctx.Test.TestEthRpc("eth_sendRawTransaction", Rlp.Encode(tx, RlpBehaviors.None).Bytes.ToHexString());

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
        string serialized = await ctx.Test.TestEthRpc("eth_sendRawTransaction", rawTransaction);
        Transaction tx = Rlp.Decode<Transaction>(Bytes.FromHexString(rawTransaction))!;
        await txSender.Received().SendTransaction(tx, TxHandlingOptions.PersistentBroadcast);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"transaction invalid, InvalidTxSignature: Signature is invalid.\"},\"id\":67}"));
    }

    [Test]
    public async Task Send_raw_transaction_returns_invalid_rlp_for_empty_list()
    {
        using Context ctx = await Context.Create();

        string serialized = await ctx.Test.TestEthRpc("eth_sendRawTransaction", "c0");

        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"Invalid RLP.\"},\"id\":67}"));
    }

    [TestCaseSource(nameof(SendRawTransactionSyncFailureCases))]
    public async Task EthSendRawTransactionSync_WhenSubmitFailsOrTimesOut_ReturnsExpectedError(
        string rawTxHex, string? timeoutMs, int expectedCode, string expectedMessageFragment)
    {
        using Context ctx = await Context.Create();
        string serialized = timeoutMs is null
            ? await ctx.Test.TestEthRpc("eth_sendRawTransactionSync", rawTxHex)
            : await ctx.Test.TestEthRpc("eth_sendRawTransactionSync", rawTxHex, timeoutMs);

        Assert.That(serialized, Does.Contain($"\"code\":{expectedCode}"));
        Assert.That(serialized, Does.Contain(expectedMessageFragment));
    }

    private static IEnumerable<TestCaseData> SendRawTransactionSyncFailureCases()
    {
        yield return new TestCaseData("c0", null, ErrorCodes.TransactionRejected, "Invalid RLP")
            .SetName("InvalidRlp");

        Transaction tx = Build.A.Transaction
            .WithNonce(3)
            .WithGasLimit(21_000)
            .WithGasPrice(20.GWei)
            .To(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        string raw = TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true);
        yield return new TestCaseData(raw, "100", ErrorCodes.Timeout, "not included within 100ms")
            .SetName("Timeout");
    }

    [Test]
    public async Task EthSendRawTransactionSync_WhenAlreadyMined_FastPathReturnsReceipt()
    {
        Transaction tx = Build.A.Transaction
            .WithNonce(3)
            .WithGasLimit(21_000)
            .WithGasPrice(20.GWei)
            .To(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Hash256 txHash = tx.Hash!;
        TxReceipt receipt = Build.A.Receipt
            .WithBlockNumber(1)
            .WithBlockHash(TestItem.KeccakA)
            .WithTransactionHash(txHash)
            .WithLogs([])
            .TestObject;

        ITxSender txSender = Substitute.For<ITxSender>();
        txSender.SendTransaction(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>())
            .Returns((txHash, AcceptTxResult.Accepted));

        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetTxReceiptInfo(txHash)
            .Returns((receipt, 0UL, new TxGasInfo(20.GWei, null, null), 0));

        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithBlockchainBridge(bridge).WithTxSender(txSender).Build();

        string raw = TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true);
        string serialized = await test.TestEthRpc("eth_sendRawTransactionSync", raw);

        Assert.That(serialized, Does.Contain($"\"transactionHash\":\"{txHash}\""));
        Assert.That(serialized, Does.Not.Contain("\"error\":"));
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
        Transaction tx = Build.A.Transaction.WithNonce(0).TestObject;
        TransactionForRpc rpcTx = TransactionForRpc.FromTransaction(tx);
        string serialized = await ctx.Test.TestEthRpc("eth_sendTransaction", rpcTx);
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
        LegacyTransactionForRpc rpcTx = (LegacyTransactionForRpc)TransactionForRpc.FromTransaction(tx);
        rpcTx.Nonce = null;
        string serialized = await ctx.Test.TestEthRpc("eth_sendTransaction", rpcTx);

        await txSender.Received().SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce);
        Assert.That(serialized, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{TestItem.KeccakA.Bytes.ToHexString(true)}\",\"id\":67}}"));
    }

    [Test]
    public async Task Send_transaction_should_return_ErrorCode_if_tx_not_added()
    {
        using Context ctx = await Context.Create();
        Transaction tx = Build.A.Transaction.WithValue(10000).SignedAndResolved(new PrivateKey("0x0000000000000000000000000000000000000000000000000000000000000001")).WithNonce(0).TestObject;
        TransactionForRpc txForRpc = TransactionForRpc.FromTransaction(tx);

        string serialized = await ctx.Test.TestEthRpc("eth_sendTransaction", txForRpc);

        Assert.That(serialized, Is.EqualTo($$"""{"jsonrpc":"2.0","error":{"code":-32000,"message":"{{TxErrorMessages.InsufficientFundsForGas}}, Balance is zero, cannot pay gas"},"id":67}"""));
    }

    public enum AccessListProvided
    {
        None,
        Partial,
        Full
    }

    [TestCase(AccessListProvided.None, false, 2, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0xbd770416a3345f91e4b34576cb804a576fa48eb1\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]},{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0xf71b\"},\"id\":67}")]
    [TestCase(AccessListProvided.Full, false, 2, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0xfffffffffffffffffffffffffffffffffffffffe\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]},{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]},{\"address\":\"0xbd770416a3345f91e4b34576cb804a576fa48eb1\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]}],\"gasUsed\":\"0x10f53\"},\"id\":67}")]
    [TestCase(AccessListProvided.Partial, false, 2, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]},{\"address\":\"0xbd770416a3345f91e4b34576cb804a576fa48eb1\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]}],\"gasUsed\":\"0xf71b\"},\"id\":67}")]

    [TestCase(AccessListProvided.None, true, 2, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0xbd770416a3345f91e4b34576cb804a576fa48eb1\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]},{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0xf71b\"},\"id\":67}")]
    [TestCase(AccessListProvided.Full, true, 2, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0xfffffffffffffffffffffffffffffffffffffffe\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]},{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]},{\"address\":\"0xbd770416a3345f91e4b34576cb804a576fa48eb1\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]}],\"gasUsed\":\"0x10f53\"},\"id\":67}")]
    [TestCase(AccessListProvided.Partial, true, 2, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]},{\"address\":\"0xbd770416a3345f91e4b34576cb804a576fa48eb1\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]}],\"gasUsed\":\"0xf71b\"},\"id\":67}")]

    [TestCase(AccessListProvided.None, true, 12, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0xbd770416a3345f91e4b34576cb804a576fa48eb1\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\",\"0x0000000000000000000000000000000000000000000000000000000000000003\",\"0x0000000000000000000000000000000000000000000000000000000000000004\",\"0x0000000000000000000000000000000000000000000000000000000000000005\",\"0x0000000000000000000000000000000000000000000000000000000000000006\",\"0x0000000000000000000000000000000000000000000000000000000000000007\",\"0x0000000000000000000000000000000000000000000000000000000000000008\",\"0x0000000000000000000000000000000000000000000000000000000000000009\",\"0x000000000000000000000000000000000000000000000000000000000000000a\",\"0x000000000000000000000000000000000000000000000000000000000000000b\",\"0x000000000000000000000000000000000000000000000000000000000000000c\"]},{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0x14739\"},\"id\":67}")]
    [TestCase(AccessListProvided.Full, true, 12, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0xfffffffffffffffffffffffffffffffffffffffe\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]},{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]},{\"address\":\"0xbd770416a3345f91e4b34576cb804a576fa48eb1\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\",\"0x0000000000000000000000000000000000000000000000000000000000000003\",\"0x0000000000000000000000000000000000000000000000000000000000000004\",\"0x0000000000000000000000000000000000000000000000000000000000000005\",\"0x0000000000000000000000000000000000000000000000000000000000000006\",\"0x0000000000000000000000000000000000000000000000000000000000000007\",\"0x0000000000000000000000000000000000000000000000000000000000000008\",\"0x0000000000000000000000000000000000000000000000000000000000000009\",\"0x000000000000000000000000000000000000000000000000000000000000000a\",\"0x000000000000000000000000000000000000000000000000000000000000000b\",\"0x000000000000000000000000000000000000000000000000000000000000000c\"]}],\"gasUsed\":\"0x15f71\"},\"id\":67}")]
    [TestCase(AccessListProvided.Partial, true, 12, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]},{\"address\":\"0xbd770416a3345f91e4b34576cb804a576fa48eb1\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\",\"0x0000000000000000000000000000000000000000000000000000000000000003\",\"0x0000000000000000000000000000000000000000000000000000000000000004\",\"0x0000000000000000000000000000000000000000000000000000000000000005\",\"0x0000000000000000000000000000000000000000000000000000000000000006\",\"0x0000000000000000000000000000000000000000000000000000000000000007\",\"0x0000000000000000000000000000000000000000000000000000000000000008\",\"0x0000000000000000000000000000000000000000000000000000000000000009\",\"0x000000000000000000000000000000000000000000000000000000000000000a\",\"0x000000000000000000000000000000000000000000000000000000000000000b\",\"0x000000000000000000000000000000000000000000000000000000000000000c\"]}],\"gasUsed\":\"0x14739\"},\"id\":67}")]

    [TestCase(AccessListProvided.None, true, 17, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0xbd770416a3345f91e4b34576cb804a576fa48eb1\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\",\"0x0000000000000000000000000000000000000000000000000000000000000003\",\"0x0000000000000000000000000000000000000000000000000000000000000004\",\"0x0000000000000000000000000000000000000000000000000000000000000005\",\"0x0000000000000000000000000000000000000000000000000000000000000006\",\"0x0000000000000000000000000000000000000000000000000000000000000007\",\"0x0000000000000000000000000000000000000000000000000000000000000008\",\"0x0000000000000000000000000000000000000000000000000000000000000009\",\"0x000000000000000000000000000000000000000000000000000000000000000a\",\"0x000000000000000000000000000000000000000000000000000000000000000b\",\"0x000000000000000000000000000000000000000000000000000000000000000c\",\"0x000000000000000000000000000000000000000000000000000000000000000d\",\"0x000000000000000000000000000000000000000000000000000000000000000e\",\"0x000000000000000000000000000000000000000000000000000000000000000f\",\"0x0000000000000000000000000000000000000000000000000000000000000010\",\"0x0000000000000000000000000000000000000000000000000000000000000011\"]},{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]}],\"gasUsed\":\"0x16f48\"},\"id\":67}")]
    [TestCase(AccessListProvided.Full, true, 17, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0xfffffffffffffffffffffffffffffffffffffffe\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\"]},{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]},{\"address\":\"0xbd770416a3345f91e4b34576cb804a576fa48eb1\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\",\"0x0000000000000000000000000000000000000000000000000000000000000003\",\"0x0000000000000000000000000000000000000000000000000000000000000004\",\"0x0000000000000000000000000000000000000000000000000000000000000005\",\"0x0000000000000000000000000000000000000000000000000000000000000006\",\"0x0000000000000000000000000000000000000000000000000000000000000007\",\"0x0000000000000000000000000000000000000000000000000000000000000008\",\"0x0000000000000000000000000000000000000000000000000000000000000009\",\"0x000000000000000000000000000000000000000000000000000000000000000a\",\"0x000000000000000000000000000000000000000000000000000000000000000b\",\"0x000000000000000000000000000000000000000000000000000000000000000c\",\"0x000000000000000000000000000000000000000000000000000000000000000d\",\"0x000000000000000000000000000000000000000000000000000000000000000e\",\"0x000000000000000000000000000000000000000000000000000000000000000f\",\"0x0000000000000000000000000000000000000000000000000000000000000010\",\"0x0000000000000000000000000000000000000000000000000000000000000011\"]}],\"gasUsed\":\"0x18780\"},\"id\":67}")]
    [TestCase(AccessListProvided.Partial, true, 17, "{\"jsonrpc\":\"2.0\",\"result\":{\"accessList\":[{\"address\":\"0x76e68a8696537e4141926f3e528733af9e237d69\",\"storageKeys\":[]},{\"address\":\"0xbd770416a3345f91e4b34576cb804a576fa48eb1\",\"storageKeys\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"0x0000000000000000000000000000000000000000000000000000000000000002\",\"0x0000000000000000000000000000000000000000000000000000000000000003\",\"0x0000000000000000000000000000000000000000000000000000000000000004\",\"0x0000000000000000000000000000000000000000000000000000000000000005\",\"0x0000000000000000000000000000000000000000000000000000000000000006\",\"0x0000000000000000000000000000000000000000000000000000000000000007\",\"0x0000000000000000000000000000000000000000000000000000000000000008\",\"0x0000000000000000000000000000000000000000000000000000000000000009\",\"0x000000000000000000000000000000000000000000000000000000000000000a\",\"0x000000000000000000000000000000000000000000000000000000000000000b\",\"0x000000000000000000000000000000000000000000000000000000000000000c\",\"0x000000000000000000000000000000000000000000000000000000000000000d\",\"0x000000000000000000000000000000000000000000000000000000000000000e\",\"0x000000000000000000000000000000000000000000000000000000000000000f\",\"0x0000000000000000000000000000000000000000000000000000000000000010\",\"0x0000000000000000000000000000000000000000000000000000000000000011\"]}],\"gasUsed\":\"0x16f48\"},\"id\":67}")]

    public async Task Eth_create_access_list_sample(AccessListProvided accessListProvided, bool optimize, long loads, string expected)
    {
        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(new TestSpecProvider(Berlin.Instance));

        (byte[] code, AccessListForRpc _) = GetTestAccessList(loads);

        AccessListTransactionForRpc transaction = test.JsonSerializer.Deserialize<AccessListTransactionForRpc>($"{{\"type\":\"0x1\", \"data\": \"{code.ToHexString(true)}\"}}")!;

        if (accessListProvided != AccessListProvided.None)
        {
            transaction.AccessList = GetTestAccessList(2, accessListProvided == AccessListProvided.Full).AccessList;
        }

        string serialized = await test.TestEthRpc("eth_createAccessList", transaction, "0x0", null, optimize);
        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public async Task Eth_createAccessList_cannot_exceed_gas_cap()
    {
        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(new TestSpecProvider(Berlin.Instance));
        ulong gasCap = 60_000;
        test.RpcConfig.GasCap = gasCap;

        // Contract creation with infinite loop; gas 200K should be capped to 60K
        TransactionForRpc transaction = test.JsonSerializer.Deserialize<TransactionForRpc>(
            $"{{\"from\": \"{SecondaryTestAddress}\", \"gasPrice\": \"0x0\", \"gas\": \"0x30D40\", \"data\": \"{InfiniteLoopCode.ToHexString(true)}\"}}")!;

        string serialized = await test.TestEthRpc("eth_createAccessList", transaction, "latest", null, true);

        ulong gasUsed = Convert.ToUInt64(JToken.Parse(serialized).SelectToken("result.gasUsed")!.Value<string>(), 16);
        Assert.That(gasUsed, Is.LessThanOrEqualTo(gasCap));
    }

    [Test]
    public async Task Eth_createAccessList_without_gas_defaults_to_gas_cap_not_block_gas_limit()
    {
        using Context ctx = await Context.Create();

        ulong blockGasLimit = ctx.Test.BlockTree.FindHeadBlock()!.Header.GasLimit;
        ulong gasCap = blockGasLimit + 500_000;
        ctx.Test.RpcConfig.GasCap = gasCap;

        // Inject infinite-loop contract — with no gas field it should consume all of gasCap, not blockGasLimit
        object stateOverride = JsonSerializer.Deserialize<object>(
            $"{{\"0xc200000000000000000000000000000000000000\":{{\"code\":\"{InfiniteLoopCode.ToHexString(true)}\"}}}}")!;

        // No gas field — should default to gasCap, not blockGasLimit
        object transaction = JsonSerializer.Deserialize<object>(
            """{"to":"0xc200000000000000000000000000000000000000"}""")!;

        string serialized = await ctx.Test.TestEthRpc("eth_createAccessList", transaction, "latest", stateOverride, false);

        ulong gasUsed = Convert.ToUInt64(JToken.Parse(serialized).SelectToken("result.gasUsed")!.Value<string>(), 16);
        Assert.That(gasUsed, Is.GreaterThan(blockGasLimit),
            $"gas used ({gasUsed}) should reflect gasCap ({gasCap}), not block gas limit ({blockGasLimit})");
    }

    [Test]
    public async Task Eth_create_access_list_with_state_override()
    {
        using Context ctx = await Context.Create();

        object transaction = JsonSerializer.Deserialize<object>(
            """{"from":"0x7f554713be84160fdf0178cc8df86f5aabd33397","to":"0xc200000000000000000000000000000000000000"}""")!;

        // PUSH1 0x01, SLOAD, POP, STOP — reads storage slot 1
        object stateOverride = JsonSerializer.Deserialize<object>(
            """{"0xc200000000000000000000000000000000000000":{"code":"0x6001545000"}}""")!;

        string withOverride = await ctx.Test.TestEthRpc("eth_createAccessList", transaction, "latest", stateOverride, false);
        string withoutOverride = await ctx.Test.TestEthRpc("eth_createAccessList", transaction, "latest", null, false);

        JToken withOverrideResult = JToken.Parse(withOverride);
        JToken withoutOverrideResult = JToken.Parse(withoutOverride);

        Assert.That(withOverrideResult, Is.Not.EqualTo(withoutOverrideResult));
        Assert.That(withOverrideResult.SelectToken("result.accessList")!.ToString(),
            Does.Contain("0x0000000000000000000000000000000000000000000000000000000000000001"));
    }

    private static async Task<(JToken Result, long GasUsed)> CallCreateAccessList(
        Context ctx, string txJson, string? stateOverrideJson, bool optimize)
    {
        object tx = JsonSerializer.Deserialize<object>(txJson)!;
        object? stateOverride = stateOverrideJson is null
            ? null
            : JsonSerializer.Deserialize<object>(stateOverrideJson);
        string serialized = await ctx.Test.TestEthRpc(
            "eth_createAccessList", tx, "latest", stateOverride, optimize);
        JToken result = JToken.Parse(serialized)["result"]!;
        long gasUsed = Convert.ToInt64(result["gasUsed"]!.Value<string>(), 16);
        return (result, gasUsed);
    }

    [Test]
    public async Task Eth_createAccessList_returns_out_of_gas_when_al_intrinsic_cost_exceeds_gas_limit()
    {
        using Context ctx = await Context.Create();

        // Contract: PUSH1 1, SLOAD, POP, PUSH1 2, SLOAD, POP, STOP — touches 2 cold storage slots.
        // optimize=true → AL = {0xc200...: [slot1, slot2]} (sender excluded, it has no storage).
        // Pass 1 (cold, no AL): 21000 + 12 + 4200 + 4 = 25,216 gas — fits in 0x6A50 (27,216).
        // Pass 2 (warm + AL intrinsic 6200): intrinsic=27,200, 16 gas remain for execution → OOG on SLOAD.
        const string contractAddr = "0xc200000000000000000000000000000000000000";
        string stateOverride = $$$"""{"{{{contractAddr}}}":{"code":"0x600154506002545000"}}""";
        string transaction = $$"""{"from":"{{CreateAccessListSender}}","to":"{{contractAddr}}","gas":"0x6A50"}""";

        (JToken result, long gasUsed) = await CallCreateAccessList(ctx, transaction, stateOverride, optimize: true);

        Assert.That(result["error"]!.Value<string>(), Is.EqualTo("out of gas"));
        Assert.That(gasUsed, Is.EqualTo(0x6A50));
        Assert.That(result["accessList"]!.ToArray(), Is.Not.Empty);
    }

    [Test]
    public async Task Eth_createAccessList_gas_calculation()
    {
        using Context ctx = await Context.Create();

        // Plain ETH transfer (value=0 so no new-account charge). Sender and recipient are both
        // pre-warmed as tx.origin / tx.to; no storage is touched → empty optimized access list.
        // Geth: wantGas=21000, wantAL=`[]`
        string transaction = $$"""{"from":"{{CreateAccessListSender}}","to":"0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","gas":"0x5208"}""";

        (JToken result, long gasUsed) = await CallCreateAccessList(ctx, transaction, stateOverrideJson: null, optimize: true);

        Assert.That(result["error"], Is.Null);
        Assert.That(gasUsed, Is.EqualTo(21_000));
        Assert.That(result["accessList"]!.ToArray(), Is.Empty);
    }

    [Test]
    public async Task Eth_createAccessList_gas_calculation_reverting_sstore_returns_access_list_and_vm_error()
    {
        using Context ctx = await Context.Create();

        // Contract creation that writes to storage (SSTORE slot 0x81) then reverts.
        // Bytecode: PUSH1 0x80, PUSH1 0x80, PUSH1 0x80, PUSH1 0x81, SSTORE, REVERT
        // This mirrors Geth's wantVMErr="execution reverted" + wantAL with 1 addr and 1 storage key.
        string transaction = $$"""{"from":"{{CreateAccessListSender}}","gas":"0x186A0","data":"0x608060806080608155fd"}""";

        (JToken result, long gasUsed) = await CallCreateAccessList(ctx, transaction, stateOverrideJson: null, optimize: true);

        Assert.That(result["error"]!.Value<string>(), Is.EqualTo("execution reverted"));
        Assert.That(gasUsed, Is.EqualTo(77496));
        // AL must contain the newly created contract address with storage key 0x81.
        // Contract address is deterministic: keccak256(rlp([sender, nonce=0]))[12:]
        Address expectedContract = ContractAddress.From(new Address(CreateAccessListSender), UInt256.Zero);
        JToken[] accessList = result["accessList"]!.ToArray();
        Assert.That(accessList, Has.Length.EqualTo(1));
        Assert.That(accessList[0]["address"]!.Value<string>(), Is.EqualTo(expectedContract.ToString().ToLowerInvariant()));
        Assert.That(
            accessList[0]["storageKeys"]!.ToArray().Count(static k => k.Value<string>() == "0x0000000000000000000000000000000000000000000000000000000000000081"),
            Is.EqualTo(1));
    }

    [Test]
    public async Task Eth_createAccessList_optimize_false_includes_sender_in_access_list()
    {
        using Context ctx = await Context.Create();
        const string contractAddr = "0xc200000000000000000000000000000000000000";
        string stateOverride = $$$"""{"{{{contractAddr}}}":{"code":"0x6001545000"}}""";
        string transaction = $$"""{"from":"{{CreateAccessListSender}}","to":"{{contractAddr}}"}""";

        (JToken result, long gasUsed) = await CallCreateAccessList(ctx, transaction, stateOverride, optimize: false);

        Assert.That(result["error"], Is.Null);
        Assert.That(gasUsed, Is.EqualTo(27_805));
        JToken[] accessList = result["accessList"]!.ToArray();
        Assert.That(accessList.Any(e => e["address"]!.Value<string>() == CreateAccessListSender), Is.True);
        // Contract with slot 1 must also appear.
        Assert.That(accessList.Any(e =>
            e["address"]!.Value<string>() == contractAddr &&
            e["storageKeys"]!.ToArray().Any(
                k => k.Value<string>() == "0x0000000000000000000000000000000000000000000000000000000000000001")),
            Is.True);
    }

    [TestCase(null)]
    [TestCase(0UL)]
    public static void ToTransaction_uses_ulong_max_when_gasCap_is_null_or_zero(ulong? gasCap)
    {
        LegacyTransactionForRpc rpcTx = new();

        Transaction tx = (Transaction)rpcTx.ToTransaction(gasCap: gasCap);

        Assert.That(tx.GasLimit, Is.EqualTo(ulong.MaxValue), "GasLimit must default to ulong.MaxValue when gasCap is null or 0");
    }

    [Test]
    public static void ToTransaction_defaults_sender_to_zero_when_from_is_null()
    {
        LegacyTransactionForRpc rpcTx = new();

        Transaction tx = (Transaction)rpcTx.ToTransaction();

        Assert.That(tx.SenderAddress, Is.EqualTo(Address.Zero), "SenderAddress must default to Address.Zero when From is null");
    }

    [TestCase(null, null, ulong.MaxValue)]
    [TestCase(null, 0UL, ulong.MaxValue)]
    [TestCase(null, 1_000_000UL, 1_000_000UL)]
    [TestCase(0UL, null, 0UL)]
    [TestCase(0UL, 1_000_000UL, 0UL)]
    [TestCase(50_000UL, null, 50_000UL)]
    [TestCase(50_000UL, 0UL, 50_000UL)]
    [TestCase(50_000UL, 100_000UL, 50_000UL)]
    [TestCase(200_000UL, 100_000UL, 100_000UL)]
    public static void ToTransaction_caps_and_defaults_gas(ulong? gas, ulong? gasCap, object expectedGasLimit)
    {
        LegacyTransactionForRpc rpcTx = new() { Gas = gas };

        Transaction tx = (Transaction)rpcTx.ToTransaction(gasCap: gasCap);

        Assert.That(tx.GasLimit, Is.EqualTo(Convert.ToUInt64(expectedGasLimit)));
    }

    [Test]
    public async Task eth_getBlockByNumber_should_return_withdrawals_correctly()
    {
        using Context ctx = await Context.Create();
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();

        Block block = Build.A.Block.WithNumber(1)
            .WithStateRoot(new Hash256("0xe4a589578a2838164c89c02e38528d7865b132981a9793a8f0b443fcfe70728f"))
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
        receiptFinder.Get(Arg.Any<Hash256>()).Returns(receiptsTab);

        ctx.Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockFinder).WithReceiptFinder(receiptFinder).Build();
        string result = await ctx.Test.TestEthRpc("eth_getBlockByNumber", TestItem.KeccakA.ToString(), "true");

        Assert.That((new EthereumJsonSerializer().Serialize(new
        {
            jsonrpc = "2.0",
            result = new BlockForRpc(block, true, Substitute.For<ISpecProvider>()),
            id = 67
        })), Is.EqualTo(result));
    }


    [Test]
    public async Task eth_sendRawTransaction_sender_with_non_delegated_code_is_rejected()
    {
        TestSpecProvider specProvider = new(Prague.Instance);
        specProvider.AllowTestChainOverride = false;

        TestRpcBlockchain Test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(specProvider);

        Transaction testTx = Build.A.Transaction
          .WithType(TxType.SetCode)
          .WithNonce(Test.ReadOnlyState.GetNonce(TestItem.AddressA))
          .WithMaxFeePerGas(9.GWei)
          .WithMaxPriorityFeePerGas(9.GWei)
          .WithGasLimit(GasCostOf.Transaction + GasCostOf.NewAccount)
          .WithAuthorizationCodeIfAuthorizationListTx()
          .WithTo(TestItem.AddressA)
          .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

        string result = await Test.TestEthRpc("eth_sendRawTransaction", Bytes.ToHexString(Rlp.Encode(testTx).Bytes));

        JsonRpcErrorResponse actual = new EthereumJsonSerializer().Deserialize<JsonRpcErrorResponse>(result)!;
        Assert.That(actual.Error!.Message, Does.Contain(AcceptTxResult.SenderIsContract.ToString()));
    }


    [Test]
    public async Task eth_sendRawTransaction_sender_with_delegated_code_is_accepted()
    {
        TestSpecProvider specProvider = new(Prague.Instance);
        specProvider.AllowTestChainOverride = false;

        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(specProvider);
        Transaction setCodeTx = Build.A.Transaction
          .WithType(TxType.SetCode)
          .WithNonce(test.ReadOnlyState.GetNonce(TestItem.AddressB))
          .WithMaxFeePerGas(9.GWei)
          .WithMaxPriorityFeePerGas(9.GWei)
          .WithGasLimit(GasCostOf.Transaction + GasCostOf.NewAccount)
          .WithAuthorizationCode(test.EthereumEcdsa.Sign(TestItem.PrivateKeyB, 0, TestItem.AddressC, test.ReadOnlyState.GetNonce(TestItem.AddressB) + 1))
          .WithTo(TestItem.AddressA)
          .SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        await test.AddBlock(setCodeTx);

        byte[]? code = test.ReadOnlyState.GetCode(TestItem.AddressB);

        Assert.That(code!.Slice(0, 3), Is.EquivalentTo(Eip7702Constants.DelegationHeader.ToArray()));

        Transaction normalTx = Build.A.Transaction
          .WithNonce(test.ReadOnlyState.GetNonce(TestItem.AddressB))
          .WithMaxFeePerGas(9.GWei)
          .WithMaxPriorityFeePerGas(9.GWei)
          .WithGasLimit(GasCostOf.Transaction)
          .WithTo(TestItem.AddressA)
          .SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        string result = await test.TestEthRpc("eth_sendRawTransaction", Bytes.ToHexString(Rlp.Encode(normalTx).Bytes));

        JsonRpcSuccessResponse actual = new EthereumJsonSerializer().Deserialize<JsonRpcSuccessResponse>(result)!;
        Assert.That(actual.Result, Is.Not.Null);
    }

    [Test]
    public async Task eth_sendRawTransaction_returns_correct_error_if_AuthorityTuple_has_null_value()
    {
        TestSpecProvider specProvider = new(Prague.Instance);
        specProvider.AllowTestChainOverride = false;

        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(specProvider);
        Transaction invalidSetCodeTx = Build.A.Transaction
          .WithType(TxType.SetCode)
          .WithNonce(test.ReadOnlyState.GetNonce(TestItem.AddressB))
          .WithMaxFeePerGas(9.GWei)
          .WithMaxPriorityFeePerGas(9.GWei)
          .WithGasLimit(GasCostOf.Transaction + GasCostOf.NewAccount)
          .WithAuthorizationCode(new AllowNullAuthorizationTuple(0, null, 0, new Signature(new byte[65])))
          .WithTo(TestItem.AddressA)
          .SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        string result = await test.TestEthRpc("eth_sendRawTransaction", Bytes.ToHexString(Rlp.Encode(invalidSetCodeTx).Bytes));

        JsonRpcErrorResponse actual = new EthereumJsonSerializer().Deserialize<JsonRpcErrorResponse>(result)!;
        Assert.That(actual.Error!.Code, Is.EqualTo(ErrorCodes.TransactionRejected));
    }

    [Test]
    public async Task eth_getTransactionByHash_returns_correct_values_on_SetCode_tx()
    {
        TestSpecProvider specProvider = new(Prague.Instance);
        specProvider.AllowTestChainOverride = false;

        TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(specProvider);

        const int BadYparity = 229;
        const int BadR = 123;
        const int BadS = 123;
        AuthorizationTuple authTuple = new(0, Address.SystemUser, 0, BadYparity, BadR, BadS);
        Transaction setCodeTx = Build.A.Transaction
          .WithType(TxType.SetCode)
          .WithNonce(test.ReadOnlyState.GetNonce(TestItem.AddressB))
          .WithMaxFeePerGas(9.GWei)
          .WithMaxPriorityFeePerGas(9.GWei)
          .WithGasLimit(GasCostOf.Transaction + GasCostOf.NewAccount)
          .WithAuthorizationCode(authTuple)
          .WithTo(TestItem.AddressA)
          .SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        await test.AddBlock(setCodeTx!);

        string jsonFromRpc = await test.TestEthRpc("eth_getTransactionByHash", setCodeTx!.CalculateHash());

        SetCodeTransactionForRpc actual = new EthereumJsonSerializer().Deserialize<JsonRpcResponse<SetCodeTransactionForRpc>>(jsonFromRpc)!.Result!;

        AuthorizationListForRpc.RpcAuthTuple result = actual.AuthorizationList!.First();

        Assert.That(result.YParity, Is.EqualTo(BadYparity), "Y parity should match the one in the transaction");
        Assert.That((int)result.R, Is.EqualTo(BadR), "R should match the one in the transaction");
        Assert.That((int)result.S, Is.EqualTo(BadS), "S should match the one in the transaction");
    }



    [Test]
    public async Task Eth_get_block_by_number_pruned()
    {
        using Context ctx = await Context.CreateWithAncientBarriers(10000);
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockByNumber", "0xF4240", "false");
        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":null,"id":67}"""));
    }

    [Test]
    public async Task Eth_tx_count_by_number_pruned()
    {
        using Context ctx = await Context.CreateWithAncientBarriers(10000);
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockTransactionCountByNumber", "0x64");
        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":null,"id":67}"""));
    }

    [Test]
    public async Task Eth_get_uncle_count_by_block_number_pruned()
    {
        using Context ctx = await Context.CreateWithAncientBarriers(10000);
        string serialized = await ctx.Test.TestEthRpc("eth_getUncleCountByBlockNumber", "0x64");
        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":null,"id":67}"""));
    }

    [Test]
    public async Task Eth_get_transaction_by_block_number_and_index_pruned()
    {
        using Context ctx = await Context.CreateWithAncientBarriers(10000);
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionByBlockNumberAndIndex", "0x100", "1");
        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":null,"id":67}"""));
    }

    [Test]
    public async Task Eth_get_block_by_hash_pruned()
    {
        using Context ctx = await Context.CreateWithAncientBarriers(10000);
        ctx.Test.Container.Resolve<IBlockStore>().Delete(ctx.Test.BlockTree.Genesis!.Number, ctx.Test.BlockTree.Genesis!.Hash!);
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockByHash", ctx.Test.BlockTree.Genesis!.Hash!.ToString(), "true");
        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":null,"id":67}"""));
    }

    [Test]
    public async Task Eth_tx_count_by_hash_pruned()
    {
        using Context ctx = await Context.CreateWithAncientBarriers(10000);
        ctx.Test.Container.Resolve<IBlockStore>().Delete(ctx.Test.BlockTree.Genesis!.Number, ctx.Test.BlockTree.Genesis!.Hash!);
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockTransactionCountByHash", ctx.Test.BlockTree.Genesis!.Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":null,"id":67}"""));
    }

    [Test]
    public async Task Eth_uncle_count_by_hash_pruned()
    {
        using Context ctx = await Context.CreateWithAncientBarriers(10000);
        ctx.Test.Container.Resolve<IBlockStore>().Delete(ctx.Test.BlockTree.Genesis!.Number, ctx.Test.BlockTree.Genesis!.Hash!);
        string serialized = await ctx.Test.TestEthRpc("eth_getUncleCountByBlockHash", ctx.Test.BlockTree.Genesis!.Hash!.ToString());
        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":null,"id":67}"""));
    }

    [Test]
    public async Task Eth_get_transaction_by_block_hash_and_index_pruned()
    {
        using Context ctx = await Context.CreateWithAncientBarriers(10000);
        ctx.Test.Container.Resolve<IBlockStore>().Delete(ctx.Test.BlockTree.Genesis!.Number, ctx.Test.BlockTree.Genesis!.Hash!);
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionByBlockHashAndIndex", ctx.Test.BlockTree.Genesis!.Hash!.ToString(), "1");
        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":null,"id":67}"""));
    }

    [Test]
    public async Task Eth_getBlockReceipts_pruned()
    {
        using Context ctx = await Context.CreateWithAncientBarriers(10000);
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockReceipts", "0x100");
        Assert.That(serialized, Is.EqualTo("""{"jsonrpc":"2.0","result":null,"id":67}"""));
    }


    [Test]
    public async Task Eth_get_transaction_receipt_pruned()
    {
        using Context ctx = await Context.CreateWithAncientBarriers(10000);
        string serialized = await ctx.Test.TestEthRpc("eth_getTransactionReceipt", TestItem.KeccakA.ToString());
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":null,\"id\":67}"));
    }

    [TestCase(new byte[] { 0x48, 0x65, 0x6c, 0x6c, 0x6f, 0x20, 0x77, 0x6f, 0x72, 0x6c, 0x64 }, TestName = "AsciiMessage")]
    [TestCase(new byte[] { 0xde, 0xad, 0xbe, 0xaf }, TestName = "NonUtf8Message")]
    public async Task EthSign_WithMessage_RecoversSignerAddress(byte[] message)
    {
        using Context ctx = await Context.Create();

        //Address is auto-generated in WalletExtensions.SetupTestAccounts
        const string keyAddress = "0x7e5f4552091a69125d5dfcb7b8c2659029395bdf";
        string serialized = await ctx.Test.TestEthRpc("eth_sign", keyAddress, message.ToHexString(true));

        JsonRpcResponse<string> response = ctx.Test.JsonSerializer.Deserialize<JsonRpcResponse<string>>(serialized)!;
        Address recovered = new EthereumEcdsa(1).RecoverAddress(new Signature(response.Result!), Eip191Hasher.HashMessage(message))!;

        Assert.That(recovered, Is.EqualTo(new Address(keyAddress)));
    }

    [Test]
    public async Task Eth_get_block_access_list_by_hash()
    {
        using Context ctx = await Context.CreateWithAmsterdamEnabled();
        Hash256 blockHash = ctx.Test.BlockTree.Head!.Hash!;
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockAccessListByHash", blockHash);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"accountChanges\":[{\"address\":\"0x00000961ef480eb55e80d19ad83579a64c007002\",\"storageChanges\":[],\"storageReads\":[\"0x0\",\"0x1\",\"0x2\",\"0x3\"],\"balanceChanges\":[],\"nonceChanges\":[],\"codeChanges\":[]},{\"address\":\"0x0000bbddc7ce488642fb579f8b00f3a590007251\",\"storageChanges\":[],\"storageReads\":[\"0x0\",\"0x1\",\"0x2\",\"0x3\"],\"balanceChanges\":[],\"nonceChanges\":[],\"codeChanges\":[]},{\"address\":\"0x0000f90827f1c53a10cb7a02335b175320002935\",\"storageChanges\":[{\"key\":\"0x2\",\"changes\":{\"0\":{\"index\":0,\"value\":\"0xba5a1c6f63a87ff0a397adcae8c3d95536bbbd7831239885bb28ae6ac3f00f82\"}}}],\"storageReads\":[],\"balanceChanges\":[],\"nonceChanges\":[],\"codeChanges\":[]},{\"address\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"storageChanges\":[],\"storageReads\":[],\"balanceChanges\":[{\"index\":1,\"value\":\"0xa410\"},{\"index\":2,\"value\":\"0xf618\"}],\"nonceChanges\":[],\"codeChanges\":[]},{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"storageChanges\":[],\"storageReads\":[],\"balanceChanges\":[{\"index\":1,\"value\":\"0x3635c9adc5dea00002\"},{\"index\":2,\"value\":\"0x3635c9adc5dea00003\"}],\"nonceChanges\":[],\"codeChanges\":[]},{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageChanges\":[],\"storageReads\":[],\"balanceChanges\":[{\"index\":1,\"value\":\"0x3635c9adc5de9f5bee\"},{\"index\":2,\"value\":\"0x3635c9adc5de9f09e5\"}],\"nonceChanges\":[{\"index\":1,\"value\":\"0x2\"},{\"index\":2,\"value\":\"0x3\"}],\"codeChanges\":[]}]},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_block_access_list_by_hash_not_found()
    {
        using Context ctx = await Context.CreateWithAmsterdamEnabled();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockAccessListByHash", Hash256.Zero);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"Resource not found\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_block_access_list_by_hash_unavailable_before_fork()
    {
        using Context ctx = await Context.Create(); // Amsterdam disabled
        Hash256 blockHash = ctx.Test.BlockTree.Head!.Hash!;
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockAccessListByHash", blockHash);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"Resource not found\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_block_access_list_by_hash_pruned()
    {
        using Context ctx = await Context.CreateWithAmsterdamEnabled();
        Block head = ctx.Test.BlockTree.Head!;
        ctx.Test.Bridge.DeleteBlockAccessList(head.Number, head.Hash!);
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockAccessListByHash", head.Hash!);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":4444,\"message\":\"pruned history unavailable\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_block_access_list_by_number()
    {
        using Context ctx = await Context.CreateWithAmsterdamEnabled();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockAccessListByNumber", "0x3");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":{\"accountChanges\":[{\"address\":\"0x00000961ef480eb55e80d19ad83579a64c007002\",\"storageChanges\":[],\"storageReads\":[\"0x0\",\"0x1\",\"0x2\",\"0x3\"],\"balanceChanges\":[],\"nonceChanges\":[],\"codeChanges\":[]},{\"address\":\"0x0000bbddc7ce488642fb579f8b00f3a590007251\",\"storageChanges\":[],\"storageReads\":[\"0x0\",\"0x1\",\"0x2\",\"0x3\"],\"balanceChanges\":[],\"nonceChanges\":[],\"codeChanges\":[]},{\"address\":\"0x0000f90827f1c53a10cb7a02335b175320002935\",\"storageChanges\":[{\"key\":\"0x2\",\"changes\":{\"0\":{\"index\":0,\"value\":\"0xba5a1c6f63a87ff0a397adcae8c3d95536bbbd7831239885bb28ae6ac3f00f82\"}}}],\"storageReads\":[],\"balanceChanges\":[],\"nonceChanges\":[],\"codeChanges\":[]},{\"address\":\"0x475674cb523a0a2736b7f7534390288fce16982c\",\"storageChanges\":[],\"storageReads\":[],\"balanceChanges\":[{\"index\":1,\"value\":\"0xa410\"},{\"index\":2,\"value\":\"0xf618\"}],\"nonceChanges\":[],\"codeChanges\":[]},{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"storageChanges\":[],\"storageReads\":[],\"balanceChanges\":[{\"index\":1,\"value\":\"0x3635c9adc5dea00002\"},{\"index\":2,\"value\":\"0x3635c9adc5dea00003\"}],\"nonceChanges\":[],\"codeChanges\":[]},{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"storageChanges\":[],\"storageReads\":[],\"balanceChanges\":[{\"index\":1,\"value\":\"0x3635c9adc5de9f5bee\"},{\"index\":2,\"value\":\"0x3635c9adc5de9f09e5\"}],\"nonceChanges\":[{\"index\":1,\"value\":\"0x2\"},{\"index\":2,\"value\":\"0x3\"}],\"codeChanges\":[]}]},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_block_access_list_by_number_not_found()
    {
        using Context ctx = await Context.CreateWithAmsterdamEnabled();
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockAccessListByNumber", "0x64");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"Resource not found\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_block_access_list_by_number_unavailable_before_fork()
    {
        using Context ctx = await Context.Create(); // Amsterdam disabled
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockAccessListByNumber", "0x3");
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"Resource not found\"},\"id\":67}"));
    }

    [Test]
    public async Task Eth_get_block_access_list_by_number_pruned()
    {
        const long number = 3;
        using Context ctx = await Context.CreateWithAmsterdamEnabled();
        Hash256 blockHash = ctx.Test.BlockTree.FindLevel(number)!.BlockInfos[0].BlockHash;
        ctx.Test.Bridge.DeleteBlockAccessList(number, blockHash);
        string serialized = await ctx.Test.TestEthRpc("eth_getBlockAccessListByNumber", number);
        Assert.That(serialized, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":4444,\"message\":\"pruned history unavailable\"},\"id\":67}"));
    }

    public class AllowNullAuthorizationTuple : AuthorizationTuple
    {
        public AllowNullAuthorizationTuple(ulong chainId, Address? codeAddress, ulong nonce, Signature? sig)
            : base(chainId, Address.Zero, nonce, new Signature(new byte[65]))
        {
            CodeAddress = codeAddress!;
            AuthoritySignature = sig!;
        }
    }

    private static (byte[] ByteCode, AccessListForRpc AccessList) GetTestAccessList(long loads = 2, bool allowSystemUser = true)
    {
        AccessList.Builder builder = new();
        if (allowSystemUser)
        {
            builder.AddAddress(Address.SystemUser);
            for (int i = 0; i < (int)loads; i++)
            {
                builder.AddStorage((UInt256)(i + 1));
            }
        }
        builder.AddAddress(TestItem.AddressC);
        AccessList accessList = builder.Build();

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
        return (byteCode, AccessListForRpc.FromAccessList(accessList));
    }

    protected class Context : IDisposable
    {
        private TestRpcBlockchain? _testCtx;
        private TestRpcBlockchain? _auraCtx;

        public Func<TestRpcBlockchain> TestFactory { get; set; } = null!;
        public Func<TestRpcBlockchain> AuraTestFactory { get; set; } = null!;

        public TestRpcBlockchain Test
        {
            get => _testCtx ??= TestFactory();
            set => _testCtx = value;
        }

        public TestRpcBlockchain AuraTest
        {
            get => _auraCtx ??= AuraTestFactory();
            set => _auraCtx = value;
        }

        private Context() { }

        public static async Task<Context> CreateWithLondonEnabled()
        {
            OverridableReleaseSpec releaseSpec = new(London.Instance) { Eip1559TransitionBlock = 1 };
            TestSpecProvider specProvider = new(releaseSpec);
            return await Create(specProvider);
        }

        public static async Task<Context> CreateWithCancunEnabled()
        {
            OverridableReleaseSpec releaseSpec = new(Cancun.Instance);
            TestSpecProvider specProvider = new(releaseSpec);
            return await Create(specProvider);
        }

        public static async Task<Context> CreateWithAmsterdamEnabled()
        {
            OverridableReleaseSpec releaseSpec = new(Amsterdam.Instance);
            TestSpecProvider specProvider = new(releaseSpec);
            return await Create(specProvider);
        }

        public static async Task<Context> CreateWithAncientBarriers(ulong blockNumber) => await Create(configurer: builder =>
        {
            builder.AddDecorator<ISyncConfig>((_, config) =>
            {
                ulong cutBlock = blockNumber;
                config.AncientBodiesBarrier = cutBlock;
                config.AncientReceiptsBarrier = cutBlock;
                config.PivotNumber = cutBlock;
                config.SnapSync = true;
                return config;
            });
        });

        public static Task<Context> Create(ISpecProvider? specProvider = null,
            IBlockchainBridge? blockchainBridge = null,
            Action<ContainerBuilder>? configurer = null,
            bool? useFlatDb = null)
        {
            Action<ContainerBuilder> wrappedConfigurer = builder =>
            {
                if (specProvider is not null) builder.AddSingleton<ISpecProvider>(specProvider);
                configurer?.Invoke(builder);
            };

            return Task.FromResult(new Context
            {
                TestFactory = () => TestRpcBlockchain.ForTest(SealEngineType.NethDev)
                    .WithBlockchainBridge(blockchainBridge!)
                    .WithConfig(new JsonRpcConfig { EstimateErrorMargin = 0, Timeout = -1 })
                    .WithBlocksConfig(new BlocksConfig() { ParallelExecution = false })
                    .WithFlatDb(useFlatDb ?? (Environment.GetEnvironmentVariable("TEST_USE_FLAT") == "1"))
                    .Build(wrappedConfigurer).Result,

                AuraTestFactory = () => TestRpcBlockchain.ForTest(SealEngineType.AuRa)
                    .Build(configurer: builder =>
                    {
                        builder
                            .WithGenesisPostProcessor((block, state) =>
                                {
                                    block.Header.AuRaStep = 0;
                                    block.Header.AuRaSignature = new byte[65];
                                }
                            );
                        wrappedConfigurer(builder);
                    }).Result
            });
        }

        public void Dispose()
        {
            _testCtx?.Dispose();
            _auraCtx?.Dispose();
        }
    }

    /// <summary>
    /// Builds the state-override and transaction for EIP-7610 CREATE2 collision regression tests.
    /// </summary>
    internal static (object StateOverride, object Transaction) BuildEip7610Fixture()
    {
        byte[] initCode = Bytes.FromHexString("602a6000556001601160003960016000f300");

        Address factoryAddress = new("0xf1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1");
        byte[] create2Input = new byte[85];
        create2Input[0] = 0xff;
        factoryAddress.Bytes.CopyTo(create2Input.AsSpan(1, 20));
        // bytes 21-52: salt = 0 (already zeroed)
        Keccak.Compute(initCode).Bytes.CopyTo(create2Input.AsSpan(53, 32));
        Address contractC = new(Keccak.Compute(create2Input).Bytes[12..]);

        const string factoryBytecode =
            "0x601260376000397f0000000000000000000000000000000000000000000000000000000000000000" +
            "601260006000f5600052602060" +
            "00f3602a6000556001601160003960016000f300";

        object stateOverride = JsonSerializer.Deserialize<object>($$"""
            {
                "0xf1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1": { "code": "{{factoryBytecode}}", "balance": "0xde0b6b3a7640000" },
                "0xca11e1ca11e1ca11e1ca11e1ca11e1ca11e1ca11": { "balance": "0xde0b6b3a7640000" },
                "{{contractC}}": { "stateDiff": { "0x0000000000000000000000000000000000000000000000000000000000000000": "0x000000000000000000000000000000000000000000000000000000000000002a" } }
            }
            """)!;

        object transaction = JsonSerializer.Deserialize<object>("""
            {
                "from": "0xca11e1ca11e1ca11e1ca11e1ca11e1ca11e1ca11",
                "to": "0xf1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1",
                "gas": "0xf4240"
            }
            """)!;

        return (stateOverride, transaction);
    }
}
