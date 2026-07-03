// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [Test]
    public async Task FillTransaction_WhenFieldsMissing_FillsNonceGasFeesAndChainId()
    {
        EIP1559TransactionForRpc rpcTx = new()
        {
            From = TestItem.AddressC,
            To = TestItem.AddressB,
            Value = 1,
        };

        EIP1559TransactionForRpc filled = (EIP1559TransactionForRpc)await FillTransactionForResult(rpcTx);

        Assert.That(filled.Nonce, Is.EqualTo(0UL), "nonce must be filled from the pending-pool view of the sender");
        Assert.That(filled.Gas, Is.EqualTo((ulong)GasCostOf.Transaction), "gas must be estimated - a plain value transfer costs exactly 21000");
        Assert.That(filled.MaxPriorityFeePerGas, Is.Not.Null, "maxPriorityFeePerGas must be filled from the gas-price oracle");
        Assert.That(filled.MaxFeePerGas, Is.Not.Null, "maxFeePerGas must be filled so the tx can be submitted as-is");
        Assert.That(filled.ChainId, Is.Not.Null, "chainId must be filled so the signature is replay-protected");
    }

    [Test]
    public async Task FillTransaction_WhenLegacy_FillsGasPrice()
    {
        LegacyTransactionForRpc rpcTx = new()
        {
            From = TestItem.AddressC,
            To = TestItem.AddressB,
            Value = 1,
        };

        LegacyTransactionForRpc filled = (LegacyTransactionForRpc)await FillTransactionForResult(rpcTx);

        Assert.That(filled.GasPrice, Is.Not.Null, "a legacy tx must have its gasPrice filled from the oracle");
        Assert.That(filled.Nonce, Is.EqualTo(0UL), "nonce must still be filled for legacy txs");
        Assert.That(filled.Gas, Is.EqualTo((ulong)GasCostOf.Transaction), "gas must be estimated for legacy txs too");
    }

    [Test]
    public async Task FillTransaction_WhenCallerSuppliesFields_LeavesThemUntouched()
    {
        EIP1559TransactionForRpc rpcTx = new()
        {
            From = TestItem.AddressC,
            To = TestItem.AddressB,
            Value = 1,
            Nonce = 7UL,
            Gas = 0x76c0UL,
            MaxFeePerGas = (UInt256)0x9184e72a000,
            MaxPriorityFeePerGas = (UInt256)0x3b9aca00,
        };

        EIP1559TransactionForRpc filled = (EIP1559TransactionForRpc)await FillTransactionForResult(rpcTx);

        Assert.That(filled.Nonce, Is.EqualTo(7UL), "caller-supplied nonce must be preserved");
        Assert.That(filled.Gas, Is.EqualTo(0x76c0UL), "caller-supplied gas must be preserved - fill only touches omitted fields");
        Assert.That(filled.MaxFeePerGas, Is.EqualTo((UInt256)0x9184e72a000), "caller-supplied maxFeePerGas must be preserved");
        Assert.That(filled.MaxPriorityFeePerGas, Is.EqualTo((UInt256)0x3b9aca00), "caller-supplied maxPriorityFeePerGas must be preserved");
    }

    [Test]
    public async Task FillTransaction_WhenContractCreationHasNoData_ReturnsInvalidInput()
    {
        EIP1559TransactionForRpc rpcTx = new()
        {
            From = TestItem.AddressC,
            To = null,
            Value = 1,
        };

        using Context ctx = await Context.CreateWithCancunEnabled();
        string response = await ctx.Test.TestEthRpc("eth_fillTransaction", rpcTx);

        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.InvalidInput}"),
            "a contract creation without data cannot be filled - it must surface as InvalidInput");
    }

    [Test]
    public async Task FillTransaction_WhenBlobTxSuppliesBlobsWithoutSidecar_DerivesCommitmentsProofsAndHashes()
    {
        if (!KzgPolynomialCommitments.IsInitialized)
            await KzgPolynomialCommitments.InitializeAsync();

        BlobTransactionForRpc rpcTx = new()
        {
            From = TestItem.AddressC,
            To = TestItem.AddressB,
            Value = 0,
            Nonce = 0UL,
            Gas = 0x76c0UL,
            MaxFeePerGas = (UInt256)0x9184e72a000,
            MaxPriorityFeePerGas = (UInt256)0x3b9aca00,
            MaxFeePerBlobGas = (UInt256)1_000_000,
            // A zeroed blob is a valid set of KZG field elements; the caller only supplies the payload.
            Blobs = [new byte[131072]],
        };

        BlobTransactionForRpc filled = (BlobTransactionForRpc)await FillTransactionForResult(rpcTx);

        Assert.That(filled.Commitments, Is.Not.Null, "commitments must be derived from the caller-supplied blobs");
        Assert.That(filled.Commitments!.Length, Is.EqualTo(1), "one KZG commitment per blob");
        Assert.That(filled.Proofs, Is.Not.Null, "proofs must be derived from the caller-supplied blobs");
        Assert.That(filled.BlobVersionedHashes, Is.Not.Null, "versioned hashes must be derived from the commitments");
        Assert.That(filled.BlobVersionedHashes!.Length, Is.EqualTo(1), "one versioned hash per blob");
    }

    [Test]
    public async Task FillTransaction_WhenFromMissing_ReturnsInvalidInput()
    {
        EIP1559TransactionForRpc rpcTx = new()
        {
            From = null,
            To = TestItem.AddressB,
            Value = 1,
        };

        using Context ctx = await Context.CreateWithCancunEnabled();
        string response = await ctx.Test.TestEthRpc("eth_fillTransaction", rpcTx);

        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.InvalidInput}"),
            "a fill without a sender cannot be completed - nonce and gas are account-specific");
        Assert.That(response, Does.Contain("from address not specified"),
            "error must name the missing field so callers know what to add");
    }

    [Test]
    public async Task FillTransaction_WhenChainIdMismatchesNode_ReturnsInvalidInput()
    {
        EIP1559TransactionForRpc rpcTx = new()
        {
            From = TestItem.AddressC,
            To = TestItem.AddressB,
            Value = 1,
            Nonce = 0UL,
            Gas = 0x5208UL,
            MaxFeePerGas = (UInt256)0x9184e72a000,
            MaxPriorityFeePerGas = (UInt256)0x3b9aca00,
            ChainId = 999_999UL,
        };

        using Context ctx = await Context.CreateWithCancunEnabled();
        string response = await ctx.Test.TestEthRpc("eth_fillTransaction", rpcTx);

        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.InvalidInput}"),
            "a chain id that doesn't match the node makes the fill unusable");
        Assert.That(response, Does.Contain("invalid chain id"),
            "error must indicate the chain id mismatch");
    }

    private async Task<TransactionForRpc> FillTransactionForResult(TransactionForRpc rpcTx)
    {
        using Context ctx = await Context.CreateWithCancunEnabled();
        string serialized = await ctx.Test.TestEthRpc("eth_fillTransaction", rpcTx);
        JsonRpcResponse<ParsedFillTransactionResult> response =
            ctx.Test.JsonSerializer.Deserialize<JsonRpcResponse<ParsedFillTransactionResult>>(serialized)!;
        Assert.That(response.Result, Is.Not.Null, "precondition: filling must succeed for valid input");
        return response.Result!.Tx;
    }

    private sealed class ParsedFillTransactionResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("tx")]
        public required TransactionForRpc Tx { get; init; }
    }
}
