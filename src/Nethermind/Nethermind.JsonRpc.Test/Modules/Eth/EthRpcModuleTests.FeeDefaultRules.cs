// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    private const string HeldAccount = "0x7e5f4552091a69125d5dfcb7b8c2659029395bdf";
    private const string ForeignAccount = "0x000000000000000000000000000000000000dead";
    private const string FeeRecipient = "0x2d44c0e097f6cd0f514edac633d82e01280b4a5c";
    private const string SetCodeAuthorization = """[{"chainId":"0x1","address":"0x0000000000000000000000000000000000000001","nonce":"0x0","yParity":"0x0","r":"0x1","s":"0x1"}]""";

    [TestCase("eth_sendTransaction", TestName = "Send")]
    [TestCase("eth_signTransaction", TestName = "Sign")]
    [TestCase("eth_fillTransaction", TestName = "Fill")]
    public async Task Fee_cap_below_the_priority_fee_is_reported_in_hexadecimal(string method) =>
        Assert.That(await FeeDefaultsError(method, """{"type":"0x2","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x3b9aca00"}""", london: true),
            Is.EqualTo("maxFeePerGas (0xa) < maxPriorityFeePerGas (0x3b9aca00)"));

    [TestCase("eth_sendTransaction", """{"type":"0x2","maxFeePerGas":"0x0","maxPriorityFeePerGas":"0x0"}""", "maxFeePerGas must be non-zero", TestName = "Send, zero fee cap")]
    [TestCase("eth_signTransaction", """{"type":"0x2","maxFeePerGas":"0x0","maxPriorityFeePerGas":"0x3b9aca00"}""", "maxFeePerGas must be non-zero", TestName = "Sign, zero fee cap below the priority fee")]
    [TestCase("eth_fillTransaction", """{"type":"0x2","maxFeePerGas":"0x0","maxPriorityFeePerGas":"0x0"}""", "maxFeePerGas must be non-zero", TestName = "Fill, zero fee cap")]
    [TestCase("eth_sendTransaction", """{"gasPrice":"0x0"}""", "gasPrice must be non-zero after london fork", TestName = "Send, zero gas price")]
    [TestCase("eth_signTransaction", """{"gasPrice":"0x0"}""", "gasPrice must be non-zero after london fork", TestName = "Sign, zero gas price")]
    [TestCase("eth_fillTransaction", """{"gasPrice":"0x0"}""", "gasPrice must be non-zero after london fork", TestName = "Fill, zero gas price")]
    [TestCase("eth_sendTransaction", """{"gasPrice":"0x1","authorizationList":AUTH}""", "both gasPrice and authorizationList specified", TestName = "Send, gas price with an authorization list")]
    [TestCase("eth_signTransaction", """{"type":"0x4","gasPrice":"0x1","authorizationList":AUTH}""", "both gasPrice and authorizationList specified", TestName = "Sign, gas price with an authorization list")]
    [TestCase("eth_fillTransaction", """{"type":"0x4","gasPrice":"0x1","authorizationList":AUTH}""", "both gasPrice and authorizationList specified", TestName = "Fill, gas price with an authorization list")]
    [TestCase("eth_createAccessList", """{"type":"0x4","gasPrice":"0x1","authorizationList":AUTH}""", "both gasPrice and authorizationList specified", TestName = "Create access list, gas price with an authorization list")]
    [TestCase("eth_createAccessList", """{"gasPrice":"0x1","authorizationList":[]}""", "both gasPrice and authorizationList specified", TestName = "Create access list, gas price with an empty authorization list")]
    [TestCase("eth_createAccessList", """{"type":"0x4","gasPrice":"0x0"}""", "gasPrice must be non-zero after london fork", TestName = "Create access list, set-code type with a zero gas price")]
    public async Task Fee_default_rules_reject_the_request(string method, string feeFields, string expected) =>
        Assert.That(await FeeDefaultsError(method, feeFields.Replace("AUTH", SetCodeAuthorization), london: true), Is.EqualTo(expected));

    [TestCase("eth_sendTransaction", TestName = "Send")]
    [TestCase("eth_fillTransaction", TestName = "Fill")]
    public async Task Fee_fields_before_london_are_rejected(string method) =>
        Assert.That(await FeeDefaultsError(method, """{"type":"0x2","maxPriorityFeePerGas":"0x3b9aca00"}""", london: false),
            Is.EqualTo("maxFeePerGas and maxPriorityFeePerGas are not valid before London is active"));

    [Test]
    public async Task Fill_fee_cap_below_the_suggested_priority_fee_is_reported_in_hexadecimal()
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        string suggested = JToken.Parse(await ctx.Test.TestEthRpc("eth_maxPriorityFeePerGas"))["result"]!.Value<string>()!;
        Assume.That(suggested, Is.Not.EqualTo("0x0"), "a fee cap of 0 must sit below the suggested priority fee");

        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", FeeDefaultsRequest("""{"type":"0x2","maxFeePerGas":"0x0"}""", HeldAccount));

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo($"maxFeePerGas (0x0) < maxPriorityFeePerGas ({suggested})"), serialized);
    }

    [TestCase("""{"type":"0x2","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x3b9aca00"}""", TestName = "Fee cap below the priority fee")]
    [TestCase("""{"gasPrice":"0x0"}""", TestName = "Zero gas price")]
    [TestCase("""{"gasPrice":"0x9184e72a000"}""", TestName = "Valid request")]
    public async Task Send_from_an_account_this_node_does_not_hold_reports_an_unknown_account(string feeFields)
    {
        using Context ctx = await Context.CreateWithLondonEnabled();

        string serialized = await ctx.Test.TestEthRpc("eth_sendTransaction", FeeDefaultsRequest(feeFields, ForeignAccount));

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo("unknown account"), serialized);
    }

    [TestCase(ForeignAccount, false, """{"type":"0x2","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x3b9aca00"}""", "maxFeePerGas (0xa) < maxPriorityFeePerGas (0x3b9aca00)", TestName = "Unknown account, fee cap below the priority fee")]
    [TestCase(HeldAccount, true, """{"type":"0x2","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x3b9aca00"}""", "maxFeePerGas (0xa) < maxPriorityFeePerGas (0x3b9aca00)", TestName = "Locked account, fee cap below the priority fee")]
    [TestCase(ForeignAccount, false, """{"gasPrice":"0x0"}""", "gasPrice must be non-zero after london fork", TestName = "Unknown account, zero gas price")]
    [TestCase(HeldAccount, true, """{"gasPrice":"0x0"}""", "gasPrice must be non-zero after london fork", TestName = "Locked account, zero gas price")]
    [TestCase(ForeignAccount, false, """{"type":"0x2","maxFeePerGas":"0x2d79883d2000","maxPriorityFeePerGas":"0x3b9aca00"}""", "tx fee (1.52 ether) exceeds the configured cap (1.00 ether)", TestName = "Unknown account, fee above the cap")]
    [TestCase(HeldAccount, true, """{"type":"0x2","maxFeePerGas":"0x2d79883d2000","maxPriorityFeePerGas":"0x3b9aca00"}""", "tx fee (1.52 ether) exceeds the configured cap (1.00 ether)", TestName = "Locked account, fee above the cap")]
    [TestCase(ForeignAccount, false, """{"gasPrice":"0x9184e72a000"}""", "unknown account", TestName = "Unknown account, valid request")]
    [TestCase(HeldAccount, true, """{"gasPrice":"0x9184e72a000"}""", "authentication needed: password or unlock", TestName = "Locked account, valid request")]
    public async Task Sign_checks_the_request_before_the_account(string from, bool locked, string feeFields, string expected)
    {
        using Context ctx = await Context.CreateWithLondonEnabled();
        ctx.Test.RpcConfig.EnableEthSignTransaction = true;
        if (locked)
            ctx.Test.TestWallet.LockAccount(new Address(from));

        string serialized = await ctx.Test.TestEthRpc("eth_signTransaction", FeeDefaultsRequest(feeFields, from));

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo(expected), serialized);
    }

    [TestCase("""{"type":"0x2","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x3b9aca00"}""", "maxFeePerGas (0xa) < maxPriorityFeePerGas (0x3b9aca00)", TestName = "Fee rule broken before signing")]
    [TestCase("""{"gasPrice":"0x9184e72a000"}""", "insufficient funds for gas * price + value, Balance is zero, cannot pay gas", TestName = "Rejected by the pool after signing")]
    public async Task Send_from_a_key_the_account_list_misses_keeps_the_real_error(string feeFields, string expected)
    {
        using UnlistedKeyWallet unlisted = new(unlocked: true);
        using Context ctx = await Context.CreateWithLondonEnabled(unlisted.Wallet);

        string serialized = await ctx.Test.TestEthRpc("eth_sendTransaction", FeeDefaultsRequest(feeFields, UnlistedKeyWallet.Account));

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo(expected),
            "the node can sign for a key file the account list does not name, so its real error must not become an unknown account");
    }

    [Test]
    public async Task Sign_from_a_locked_key_the_account_list_misses_needs_authentication()
    {
        using UnlistedKeyWallet unlisted = new(unlocked: false);
        using Context ctx = await Context.CreateWithLondonEnabled(unlisted.Wallet);
        ctx.Test.RpcConfig.EnableEthSignTransaction = true;

        string serialized = await ctx.Test.TestEthRpc("eth_signTransaction", FeeDefaultsRequest("""{"gasPrice":"0x9184e72a000"}""", UnlistedKeyWallet.Account));

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo("authentication needed: password or unlock"),
            "the node holds a key for the account, it only has to be unlocked");
    }

    [TestCase("eth_sendTransaction", TestName = "Send")]
    [TestCase("eth_signTransaction", TestName = "Sign")]
    public async Task Request_from_an_account_without_a_key_file_reports_an_unknown_account(string method)
    {
        using UnlistedKeyWallet unlisted = new(unlocked: true);
        using Context ctx = await Context.CreateWithLondonEnabled(unlisted.Wallet);
        ctx.Test.RpcConfig.EnableEthSignTransaction = true;

        string serialized = await ctx.Test.TestEthRpc(method, FeeDefaultsRequest("""{"gasPrice":"0x9184e72a000"}""", ForeignAccount));

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo("unknown account"),
            "a key file named after another account must not make this one held");
    }

    [TestCase("""{"accessList":[]}""", true, false, TestName = "Access list without a gas price")]
    [TestCase("""{"gasPrice":"0x3b9aca00"}""", false, false, TestName = "Legacy with a gas price")]
    [TestCase("""{}""", false, true, TestName = "Dynamic fee without fees")]
    [TestCase("""{"maxFeePerGas":"0x77359400"}""", false, true, TestName = "Dynamic fee with only a fee cap")]
    [TestCase("""{"maxFeePerGas":"0x77359400","maxPriorityFeePerGas":"0x1"}""", false, false, TestName = "Dynamic fee with both fees")]
    [TestCase("""{"accessList":[],"gasPrice":"0x3b9aca00"}""", false, false, TestName = "Access list with a gas price")]
    [TestCase("""{"type":"0x2","gasPrice":"0x3b9aca00"}""", false, false, TestName = "Dynamic fee priced with a gas price")]
    public async Task Fill_estimates_only_the_fee_defaults_it_uses(string feeFields, bool estimatesGasPrice, bool estimatesPriorityFee)
    {
        IGasPriceOracle oracle = Substitute.For<IGasPriceOracle>();
        oracle.GetGasPriceEstimate().Returns(new ValueTask<UInt256>(1_000_000_000));
        oracle.GetMaxPriorityGasFeeEstimate().Returns((UInt256)1_000_000_000);
        using TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithGasPriceOracle(oracle).Build(new TestSpecProvider(London.Instance));

        string serialized = await test.TestEthRpc("eth_fillTransaction", FeeDefaultsRequest(feeFields, HeldAccount));

        Assert.That(JToken.Parse(serialized)["error"], Is.Null, serialized);
        _ = oracle.Received(estimatesGasPrice ? 1 : 0).GetGasPriceEstimate();
        oracle.Received(estimatesPriorityFee ? 1 : 0).GetMaxPriorityGasFeeEstimate();
    }

    [TestCase("{}", true)]
    [TestCase("""{"accessList":[]}""", false)]
    public async Task Send_without_gas_and_fees_fills_them(string feeFields, bool dynamicFee)
    {
        UInt256 suggestedFee = 1_000_000_000;
        IGasPriceOracle oracle = Substitute.For<IGasPriceOracle>();
        oracle.GetGasPriceEstimate().Returns(new ValueTask<UInt256>(suggestedFee));
        oracle.GetMaxPriorityGasFeeEstimate().Returns(suggestedFee);
        ITxSender txSender = Substitute.For<ITxSender>();
        Transaction? sent = null;
        txSender.SendTransaction(Arg.Do<Transaction>(tx => sent = tx), Arg.Any<TxHandlingOptions>())
            .Returns((TestItem.KeccakA, AcceptTxResult.Accepted));
        using TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithGasPriceOracle(oracle).WithTxSender(txSender).Build(new TestSpecProvider(London.Instance));

        JsonObject request = JsonNode.Parse(feeFields)!.AsObject();
        request["from"] = TestItem.AddressC.ToString();
        request["to"] = FeeRecipient;
        request["value"] = "0x1";
        string serialized = await test.TestEthRpc("eth_sendTransaction", JsonSerializer.Deserialize<object>(request.ToJsonString()));

        BlockHeader head = test.BlockTree.Head!.Header;
        UInt256 nextBaseFee = BaseFeeCalculator.CalculateForReferenceSlot(head, test.SpecProvider.GetSpecFor1559(head.Number + 1));

        Assert.That(JToken.Parse(serialized)["error"], Is.Null, serialized);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent!.GasLimit, Is.EqualTo((ulong)GasCostOf.Transaction), "gas must be estimated");
            Assert.That(sent.Supports1559, Is.EqualTo(dynamicFee));
            Assert.That(sent.MaxPriorityFeePerGas, Is.EqualTo(suggestedFee), "the tip must come from the gas price oracle");
            Assert.That(sent.MaxFeePerGas, Is.EqualTo(dynamicFee ? 2 * nextBaseFee + suggestedFee : suggestedFee),
                "a dynamic fee cap must be twice the next base fee plus the tip");
        }
    }

    [Test]
    public async Task Send_does_not_fill_a_blob_transaction()
    {
        IGasPriceOracle oracle = Substitute.For<IGasPriceOracle>();
        using TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .WithGasPriceOracle(oracle).Build(new TestSpecProvider(Cancun.Instance));

        await test.TestEthRpc("eth_sendTransaction", FeeDefaultsRequest(
            """{"type":"0x3","maxFeePerBlobGas":"0x1","blobVersionedHashes":["0x0100000000000000000000000000000000000000000000000000000000000000"]}""",
            HeldAccount));

        _ = oracle.DidNotReceive().GetGasPriceEstimate();
        oracle.DidNotReceive().GetMaxPriorityGasFeeEstimate();
    }

    private async Task<string?> FeeDefaultsError(string method, string feeFields, bool london)
    {
        using Context ctx = london ? await Context.CreateWithLondonEnabled() : await Context.Create();
        ctx.Test.RpcConfig.EnableEthSignTransaction = true;

        string serialized = await ctx.Test.TestEthRpc(method, FeeDefaultsRequest(feeFields, HeldAccount));

        return JToken.Parse(serialized)["error"]?["message"]?.Value<string>();
    }

    private static object? FeeDefaultsRequest(string feeFields, string from)
    {
        JsonObject request = JsonNode.Parse(feeFields)!.AsObject();
        request["from"] = from;
        request["to"] = FeeRecipient;
        request["gas"] = "0x76c0";
        request["nonce"] = "0x0";
        return JsonSerializer.Deserialize<object>(request.ToJsonString());
    }

    /// <summary>A key store wallet whose one key file is named so that the account list does not name it.</summary>
    private sealed class UnlistedKeyWallet : IDisposable
    {
        public static readonly string Account = TestItem.AddressD.ToString();

        private readonly TempPath _directory = TempPath.GetTempDirectory();

        public UnlistedKeyWallet(bool unlocked)
        {
            using SecureString passphrase = "passphrase".Secure();
            KeyStoreConfig config = new() { KeyStoreDirectory = _directory.Path, KdfparamsN = 1024 };
            FileKeyStore keyStore = new(config, new EthereumJsonSerializer(), new AesEncrypter(config, LimboLogs.Instance),
                new CryptoRandom(), LimboLogs.Instance, new PrivateKeyStoreIOSettingsProvider(config));
            keyStore.StoreKey(TestItem.PrivateKeyD, passphrase);
            string keyFile = Directory.GetFiles(_directory.Path).Single();
            File.Move(keyFile, Path.Combine(_directory.Path, $"{TestItem.AddressD.ToString(false, false)}.json"));

            Wallet = new DevKeyStoreWallet(keyStore, LimboLogs.Instance, createTestAccounts: false);
            Assert.That(Wallet.GetAccounts(), Does.Not.Contain(TestItem.AddressD), "precondition: the account list misses the key file");
            if (unlocked)
                Assert.That(Wallet.UnlockAccount(TestItem.AddressD, passphrase), Is.True, "precondition: the key file unlocks");
        }

        public DevKeyStoreWallet Wallet { get; }

        public void Dispose() => _directory.Dispose();
    }
}
