// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    // Address derived from PrivateKey 0x00..01 by WalletExtensions.SetupTestAccounts
    private const string UnlockedTestAccount = "0x7e5f4552091a69125d5dfcb7b8c2659029395bdf";
    private const string LockedAccount = "0x000000000000000000000000000000000000dead";
    private const string FeeFieldsMissingMessage = "missing gasPrice or maxFeePerGas/maxPriorityFeePerGas";
    private static readonly TxDecoder TxRlpDecoder = TxDecoder.Instance;

    [TestCase(TxType.Legacy, "gas", "gas not specified", TestName = "GasMissing")]
    [TestCase(TxType.Legacy, "gasPrice", FeeFieldsMissingMessage, TestName = "LegacyFeesMissing")]
    [TestCase(TxType.AccessList, "gasPrice", FeeFieldsMissingMessage, TestName = "AccessListFeesMissing")]
    [TestCase(TxType.EIP1559, "maxFeePerGas", FeeFieldsMissingMessage, TestName = "Eip1559MaxFeePerGasMissing")]
    [TestCase(TxType.EIP1559, "maxPriorityFeePerGas", FeeFieldsMissingMessage, TestName = "Eip1559MaxPriorityFeePerGasMissing")]
    [TestCase(TxType.SetCode, "maxFeePerGas", FeeFieldsMissingMessage, TestName = "SetCodeMaxFeePerGasMissing")]
    [TestCase(TxType.Legacy, "nonce", "nonce not specified", TestName = "LegacyNonceMissing")]
    [TestCase(TxType.AccessList, "nonce", "nonce not specified", TestName = "AccessListNonceMissing")]
    [TestCase(TxType.EIP1559, "nonce", "nonce not specified", TestName = "Eip1559NonceMissing")]
    [TestCase(TxType.SetCode, "nonce", "nonce not specified", TestName = "SetCodeNonceMissing")]
    public async Task SignTransaction_WhenRequiredFieldMissing_ReturnsInvalidInput(TxType type, string omitField, string expectedMessage)
    {
        TransactionForRpc rpcTx = BuildTx(type, omitField);
        string response = await SignTransaction(rpcTx);

        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.InvalidInput}"),
            "missing required field must surface as InvalidInput so callers can branch on it");
        Assert.That(response, Does.Contain(expectedMessage),
            "error message must be precise so callers know which field to fix");
    }

    [TestCase(LockedAccount, null, TestName = "WrongAccount")]
    [TestCase(null, "from", TestName = "FromMissing")]
    public async Task SignTransaction_WhenSenderNotUnlocked_ReturnsAuthError(string? fromOverride, string? omitField)
    {
        // Missing-from defaults to Address.Zero; both paths fail the IsUnlocked check with the same response.
        TransactionForRpc rpcTx = BuildTx(TxType.Legacy, omitField, fromOverride);
        string response = await SignTransaction(rpcTx);

        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.InvalidInput}"),
            "wallet lookup failure surfaces as -32000 to align with keystore error handling");
        Assert.That(response, Does.Contain("authentication needed: password or unlock"),
            "wording must match keystore error so tools that text-match keep working");
    }

    [Test]
    public async Task SignTransaction_WhenTotalFeeExceedsCap_ReturnsInvalidInput()
    {
        EIP1559TransactionForRpc rpcTx = (EIP1559TransactionForRpc)BuildTx(TxType.EIP1559);
        rpcTx.MaxFeePerGas = (UInt256)50_000_000_000_000UL; // 50000 gwei * 30400 gas = 1.52 ETH > 1 ETH cap
        rpcTx.MaxPriorityFeePerGas = (UInt256)1_000_000_000UL;

        string response = await SignTransaction(rpcTx);

        Assert.That(response, Does.Contain("exceeds the configured cap"),
            "fees above RpcTxFeeCap must be rejected before signing - DOS / fat-finger guard");
    }

    [Test]
    public async Task SignTransaction_WhenBlobTxMissingCommitments_ReturnsInvalidInput()
    {
        byte[] versionedHash = new byte[32];
        versionedHash[0] = 0x01;
        BlobTransactionForRpc rpcTx = new()
        {
            From = new Address(UnlockedTestAccount),
            To = new Address("0x2d44c0e097f6cd0f514edac633d82e01280b4a5c"),
            Gas = 0x76c0,
            Nonce = 0UL,
            MaxFeePerGas = (UInt256)0x9184e72a000,
            MaxPriorityFeePerGas = (UInt256)0x3b9aca00,
            MaxFeePerBlobGas = (UInt256)1_000_000,
            BlobVersionedHashes = [versionedHash],
            Blobs = [new byte[131072]],
        };

        string response = await SignTransaction(rpcTx);

        Assert.That(response, Does.Contain("commitments must be provided alongside blobs"),
            "blob signing without commitments must surface a precise error so callers know what to add");
    }

    [TestCase(false, typeof(EIP1559TransactionForRpc), TestName = "WithoutExplicitType_PromotedToEip1559")]
    [TestCase(true, typeof(LegacyTransactionForRpc), TestName = "WithExplicitLegacyType_StaysLegacy")]
    public async Task SignTransaction_LegacyShapeJson_RespectsExplicitTypePinning(bool withExplicitType, Type expectedEchoType)
    {
        // Bypasses BuildTx because constructed C# instances always serialize the `type` field;
        // we need raw JSON that omits it to drive HasExplicitType=false on the server.
        string typeLine = withExplicitType ? "\"type\": \"0x0\"," : "";
        string txJson = $$"""
            {
                {{typeLine}}
                "from": "{{UnlockedTestAccount}}",
                "to": "0x2d44c0e097f6cd0f514edac633d82e01280b4a5c",
                "value": "0x9184e72a",
                "gas": "0x76c0",
                "gasPrice": "0x9184e72a000",
                "nonce": "0x0"
            }
            """;
        JsonElement param = JsonSerializer.Deserialize<JsonElement>(txJson);

        using Context ctx = await Context.Create();
        ctx.Test.RpcConfig.EnableEthSignTransaction = true;
        string serialized = await ctx.Test.TestEthRpc("eth_signTransaction", param);
        JsonRpcResponse<ParsedSignTransactionResult> response = ctx.Test.JsonSerializer.Deserialize<JsonRpcResponse<ParsedSignTransactionResult>>(serialized)!;
        Assert.That(response.Result, Is.Not.Null, "precondition: signing must succeed for valid input");

        Assert.That(response.Result!.Tx, Is.TypeOf(expectedEchoType),
            "no-type input must auto-promote to EIP-1559; explicit type must be preserved");
    }

    [TestCase(TxType.Legacy, typeof(LegacyTransactionForRpc), TestName = "Legacy")]
    [TestCase(TxType.AccessList, typeof(AccessListTransactionForRpc), TestName = "AccessList")]
    [TestCase(TxType.EIP1559, typeof(EIP1559TransactionForRpc), TestName = "Eip1559")]
    [TestCase(TxType.SetCode, typeof(SetCodeTransactionForRpc), TestName = "SetCode")]
    public async Task SignTransaction_WhenValid_RawRoundTripsAndTxEcho(TxType type, Type expectedEchoType)
    {
        TransactionForRpc rpcTx = BuildTx(type);
        ParsedSignTransactionResult result = await SignTransactionForResult(rpcTx);

        Transaction decoded = TxRlpDecoder.DecodeCompleteNotNull(
            result.Raw,
            RlpBehaviors.AllowUnsigned | RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);

        Assert.That(decoded.Type, Is.EqualTo(type), "type must round-trip through RLP encode/decode");
        Assert.That(decoded.GasLimit, Is.EqualTo(0x76c0UL), "gas must round-trip exactly - caller provided it explicitly");
        Assert.That(decoded.Nonce, Is.EqualTo(0UL), "nonce must round-trip - caller provided it explicitly");

        Address recovered = new EthereumEcdsa(decoded.ChainId ?? 1).RecoverAddress(decoded)!;
        Assert.That(recovered, Is.EqualTo(new Address(UnlockedTestAccount)),
            "signature must recover to the from address - raw is the canonical signed artifact");

        Assert.That(result.Tx, Is.TypeOf(expectedEchoType),
            "tx echo must preserve subclass so JSON shape survives for clients that branch on type");
    }

    private async Task<string> SignTransaction(TransactionForRpc rpcTx)
    {
        using Context ctx = await Context.Create();
        ctx.Test.RpcConfig.EnableEthSignTransaction = true;
        return await ctx.Test.TestEthRpc("eth_signTransaction", rpcTx);
    }

    private async Task<ParsedSignTransactionResult> SignTransactionForResult(TransactionForRpc rpcTx)
    {
        using Context ctx = await Context.Create();
        ctx.Test.RpcConfig.EnableEthSignTransaction = true;
        string serialized = await ctx.Test.TestEthRpc("eth_signTransaction", rpcTx);
        JsonRpcResponse<ParsedSignTransactionResult> response = ctx.Test.JsonSerializer.Deserialize<JsonRpcResponse<ParsedSignTransactionResult>>(serialized)!;
        Assert.That(response.Result, Is.Not.Null, "precondition: signing must succeed for valid input");
        return response.Result!;
    }

    [Test]
    public void SignTransactionResult_Dispose_ReturnsRentedBufferToPool()
    {
        TrackingPool pool = new();
        ArrayPoolList<byte> raw = new(pool, capacity: 32, startingCount: 16);
        byte[] rented = raw.UnsafeGetInternalArray();
        SignTransactionResult result = new()
        {
            Raw = raw,
            Tx = BuildTx(TxType.EIP1559)
        };

        // Drive the same disposal path the JSON-RPC pipeline uses for a successful response —
        // a regression test for the pool-rental leak that occurs when SignTransactionResult
        // is not IDisposable: TryDispose(object?) skips non-IDisposable values, so Raw never
        // gets disposed and the rented buffer is lost to GC instead of returning to the pool.
        JsonRpcSuccessResponse response = new() { Result = result };
        response.Dispose();

        Assert.That(pool.Returned, Is.EqualTo([rented]),
            "the rented buffer must come back to the pool - otherwise pooling is strictly " +
            "worse than direct allocation");
    }

    private sealed class ParsedSignTransactionResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("raw")]
        public required byte[] Raw { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("tx")]
        public required TransactionForRpc Tx { get; init; }
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        public List<byte[]> Returned { get; } = [];
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false) => Returned.Add(array);
    }

    private static TransactionForRpc BuildTx(TxType type, string? omitField = null, string? fromOverride = null)
    {
        Address? from = fromOverride is not null
            ? new Address(fromOverride)
            : (omitField == "from" ? null : new Address(UnlockedTestAccount));

        Address to = new("0x2d44c0e097f6cd0f514edac633d82e01280b4a5c");
        UInt256 value = 0x9184e72a;
        ulong gas = 0x76c0;
        ulong nonce = 0;
        UInt256 gasPrice = 0x9184e72a000;

        return type switch
        {
            TxType.EIP1559 => new EIP1559TransactionForRpc
            {
                From = from,
                To = to,
                Value = value,
                Gas = omitField == "gas" ? null : gas,
                Nonce = omitField == "nonce" ? null : nonce,
                MaxFeePerGas = omitField == "maxFeePerGas" ? null : (UInt256?)gasPrice,
                MaxPriorityFeePerGas = omitField == "maxPriorityFeePerGas" ? null : (UInt256?)0x3b9aca00,
            },
            TxType.SetCode => new SetCodeTransactionForRpc
            {
                From = from,
                To = to,
                Value = value,
                Gas = omitField == "gas" ? null : gas,
                Nonce = omitField == "nonce" ? null : nonce,
                MaxFeePerGas = omitField == "maxFeePerGas" ? null : (UInt256?)gasPrice,
                MaxPriorityFeePerGas = omitField == "maxPriorityFeePerGas" ? null : (UInt256?)0x3b9aca00,
                AuthorizationList = new AuthorizationListForRpc(),
            },
            TxType.AccessList => new AccessListTransactionForRpc
            {
                From = from,
                To = to,
                Value = value,
                Gas = omitField == "gas" ? null : gas,
                Nonce = omitField == "nonce" ? null : nonce,
                GasPrice = omitField == "gasPrice" ? null : (UInt256?)gasPrice,
                AccessList = new AccessListForRpc(),
            },
            _ => new LegacyTransactionForRpc
            {
                From = from,
                To = to,
                Value = value,
                Gas = omitField == "gas" ? null : gas,
                Nonce = omitField == "nonce" ? null : nonce,
                GasPrice = omitField == "gasPrice" ? null : (UInt256?)gasPrice,
            },
        };
    }
}
