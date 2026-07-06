// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
    private static IEnumerable<TestCaseData> MissingFieldFillCases()
    {
        yield return new TestCaseData((TransactionForRpc)new EIP1559TransactionForRpc
        {
            From = TestItem.AddressC,
            To = TestItem.AddressB,
            Value = 1,
        }).SetName("Eip1559");

        yield return new TestCaseData((TransactionForRpc)new LegacyTransactionForRpc
        {
            From = TestItem.AddressC,
            To = TestItem.AddressB,
            Value = 1,
        }).SetName("Legacy");
    }

    [TestCaseSource(nameof(MissingFieldFillCases))]
    public async Task FillTransaction_WhenFieldsMissing_FillsNonceGasFeesAndChainId(TransactionForRpc rpcTx)
    {
        LegacyTransactionForRpc filled = (LegacyTransactionForRpc)await FillTransactionForResult(rpcTx);

        Assert.That(filled.Nonce, Is.EqualTo(0UL), "nonce must be filled from the pending-pool view of the sender");
        Assert.That(filled.Gas, Is.EqualTo((ulong)GasCostOf.Transaction), "gas must be estimated - a plain value transfer costs exactly 21000");
        Assert.That(filled.ChainId, Is.Not.Null, "chainId must be filled so the signature is replay-protected");

        if (filled is EIP1559TransactionForRpc eip1559)
        {
            Assert.That(eip1559.MaxPriorityFeePerGas, Is.Not.Null, "maxPriorityFeePerGas must be filled from the gas-price oracle");
            Assert.That(eip1559.MaxFeePerGas, Is.Not.Null, "maxFeePerGas must be filled so the tx can be submitted as-is");
        }
        else
        {
            Assert.That(filled.GasPrice, Is.Not.Null, "a legacy tx must have its gasPrice filled from the oracle");
        }
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

    private static IEnumerable<TestCaseData> InvalidInputCases()
    {
        yield return new TestCaseData(
            (TransactionForRpc)new EIP1559TransactionForRpc { From = null, To = TestItem.AddressB, Value = 1 },
            "from address not specified").SetName("FromMissing");

        yield return new TestCaseData(
            (TransactionForRpc)new EIP1559TransactionForRpc
            {
                From = TestItem.AddressC,
                To = TestItem.AddressB,
                Value = 1,
                Nonce = 0UL,
                Gas = 0x5208UL,
                MaxFeePerGas = (UInt256)0x9184e72a000,
                MaxPriorityFeePerGas = (UInt256)0x3b9aca00,
                ChainId = 999_999UL,
            },
            "invalid chain id").SetName("ChainIdMismatch");

        yield return new TestCaseData(
            (TransactionForRpc)new EIP1559TransactionForRpc { From = TestItem.AddressC, To = null, Value = 1 },
            null).SetName("ContractCreationWithoutData");
    }

    [TestCaseSource(nameof(InvalidInputCases))]
    public async Task FillTransaction_WithUnfillableInput_ReturnsInvalidInput(TransactionForRpc rpcTx, string? expectedMessage)
    {
        using Context ctx = await Context.CreateWithCancunEnabled();
        string response = await ctx.Test.TestEthRpc("eth_fillTransaction", rpcTx);

        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.InvalidInput}"),
            "unfillable input must surface as InvalidInput so callers can branch on it");
        if (expectedMessage is not null)
            Assert.That(response, Does.Contain(expectedMessage), "error must name the problem so callers know what to fix");
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
    public async Task FillTransaction_WhenBlobCountExceedsLimit_ReturnsInvalidInput()
    {
        // Seven blobs is over the Cancun per-tx limit; this must be rejected before any KZG work runs.
        byte[][] blobs = new byte[7][];
        for (int i = 0; i < blobs.Length; i++)
            blobs[i] = new byte[131072];

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
            Blobs = blobs,
        };

        using Context ctx = await Context.CreateWithCancunEnabled();
        string response = await ctx.Test.TestEthRpc("eth_fillTransaction", rpcTx);

        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.InvalidInput}"),
            "a blob count above the per-tx limit must be rejected");
    }

    [Test]
    public async Task FillTransaction_WhenSuppliedHashesDoNotMatchBlobs_ReturnsInvalidInput()
    {
        if (!KzgPolynomialCommitments.IsInitialized)
            await KzgPolynomialCommitments.InitializeAsync();

        // Correct version byte but a bogus hash body: derivation must reject it rather than overwrite.
        byte[] mismatchedHash = new byte[32];
        mismatchedHash[0] = KzgPolynomialCommitments.KzgBlobHashVersionV1;

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
            Blobs = [new byte[131072]],
            BlobVersionedHashes = [mismatchedHash],
        };

        using Context ctx = await Context.CreateWithCancunEnabled();
        string response = await ctx.Test.TestEthRpc("eth_fillTransaction", rpcTx);

        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.InvalidInput}"),
            "caller-supplied versioned hashes that don't match the blobs must be rejected, not overwritten");
        Assert.That(response, Does.Contain("blob versioned hashes do not match the supplied blobs"),
            "error must explain the hash mismatch");
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
