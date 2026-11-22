// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.LightClient.Rpc;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;
using NSubstitute;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class EvmExecutorTests
{
    private static readonly Address Contract = new("0x1234567890123456789012345678901234567890");

    [Test]
    public async Task Frame_submission_verifies_payer_prefix_and_keyed_nonce_before_relay(
        [Values("valid", "dimension-fits", "blob-valid", "nonce", "prefix", "signature", "execution-fee", "blob-fee",
            "execution-reservation", "state-reservation", "verify-gas-below", "verify-gas-limit", "verify-gas-over",
            "verify-state-below", "verify-state-limit", "verify-state-over", "prefix-work", "prefix-opcode", "prefix-opcode-abort")] string scenario)
    {
        byte[] approve = [0x60, 0x03, 0x60, 0x00, 0x60, 0x00, 0xaa, 0x00];
        byte[] code = scenario switch
        {
            "prefix" => [0x00],
            // Spend more than 100k gas before approving payment.
            "prefix-work" => [0x61, 0x17, 0x70, 0x5b, 0x60, 0x01, 0x90, 0x03, 0x80, 0x60, 0x03, 0x57, 0x50, .. approve],
            "prefix-opcode" => [0x43, 0x50, .. approve],
            "prefix-opcode-abort" => [0x43, 0x50, 0x5b, 0x60, 0x02, 0x56],
            _ => approve,
        };
        FakeSource source = new(code, contractBalance: 1_000_000_000) { BaseFee = (UInt256)(scenario == "execution-fee" ? 10 : 1) };
        SingleReleaseSpecProvider spec = new(new KeyedFrameSpec(), 1, 1);
        using EvmExecutor calls = new(source, spec, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, calls, spec, () => source.Head);
        ulong executionGas = scenario switch
        {
            "verify-gas-below" => 299_999,
            "verify-gas-limit" => 300_000,
            "verify-gas-over" => 300_001,
            _ => 200_000,
        };
        ulong stateGas = scenario switch
        {
            "state-reservation" => 300_000,
            "verify-state-below" => 499_999,
            "verify-state-limit" => 500_000,
            "verify-state-over" => 500_001,
            _ => 200_000,
        };
        Transaction transaction = CreateFrameSubmission(executionGas, stateGas);
        transaction.Nonce = scenario == "nonce" ? 41UL : 42UL;
        if (scenario == "signature")
            transaction.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, Contract, default,
                new byte[TxFrameSignature.Secp256k1SignatureLength])];
        if (scenario is "blob-valid" or "blob-fee")
        {
            await AddFrameBlobAsync(transaction);
            if (scenario == "blob-fee")
            {
                Assert.That(FrameTxValidation.TryCalculateGasBudget(transaction, spec.GenesisSpec, out _, out _, out ulong maxGas), Is.True);
                transaction.MaxFee = maxGas;
            }
        }
        byte[] encoded = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
        RlpReader reader = new(encoded);
        Transaction submitted = TxDecoder.Instance.DecodeCompleteNotNull(ref reader,
            RlpBehaviors.AllowUnsigned | RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
        Assert.That(FrameTxValidation.TryCalculateBlockGasReservations(submitted, spec.GenesisSpec,
            out ulong executionReservation, out ulong stateReservation), Is.True);
        source.BlockGasLimit = scenario switch
        {
            "dimension-fits" => Math.Max(executionReservation, stateReservation),
            "execution-reservation" => executionReservation - 1,
            "state-reservation" => stateReservation - 1,
            _ => 30_000_000,
        };
        if (scenario == "dimension-fits") Assert.That(source.BlockGasLimit, Is.LessThan(submitted.GasLimit));
        if (scenario == "state-reservation") Assert.That(executionReservation, Is.LessThanOrEqualTo(source.BlockGasLimit));
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { "0x" + Convert.ToHexString(encoded).ToLowerInvariant() });
        if (scenario is "valid" or "dimension-fits" or "blob-valid" or "verify-gas-below" or "verify-gas-limit" or "verify-state-below" or "verify-state-limit" or "prefix-work")
        {
            object? result = await rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(transaction.CalculateHash().ToString()));
                Assert.That(source.Broadcasts, Is.EqualTo(1));
                Assert.That(source.StorageFetches, Is.GreaterThan(0));
                Assert.That(source.CodeFetches, Is.EqualTo(1));
            }
            int storageFetches = source.StorageFetches;
            source.Head = new(source.Head.Slot + 1, source.Head.Number + 1, Keccak.Compute("next frame block"), Keccak.Compute("next frame state"));

            object? repeated = await rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(repeated, Is.EqualTo(transaction.CalculateHash().ToString()));
                Assert.That(source.Broadcasts, Is.EqualTo(2));
                Assert.That(source.CodeFetches, Is.EqualTo(1));
                Assert.That(source.StorageFetches, Is.EqualTo(storageFetches));
                Assert.That(source.ContractAccountFetches, Is.EqualTo(2));
            }
        }
        else
        {
            RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(error!.Code, Is.EqualTo(-32003));
                Assert.That(source.Broadcasts, Is.Zero);
                if (scenario is "prefix-opcode" or "prefix-opcode-abort")
                    Assert.That(error.Message, Does.Contain("banned opcode NUMBER"));
                if (scenario is "execution-fee" or "blob-fee" or "execution-reservation" or "state-reservation" or "verify-gas-over" or "verify-state-over")
                {
                    Assert.That(error.Message, Does.Contain(scenario switch
                    {
                        "execution-fee" or "blob-fee" => "max fee is below",
                        "verify-gas-over" or "verify-state-over" => "bounded public-mempool work limit",
                        _ => "gas reservation exceeds",
                    }));
                    Assert.That(source.CodeFetches, Is.Zero);
                    Assert.That(source.StorageFetches, Is.Zero);
                }
            }
        }
    }

    [Test]
    public async Task Frame_submission_from_a_guarded_sender_is_refused_before_relay([Values] bool guarded)
    {
        byte[] approve = [0x60, 0x03, 0x60, 0x00, 0x60, 0x00, 0xaa, 0x00];
        FakeSource source = new(approve, contractBalance: 1_000_000_000) { BaseFee = 1 };
        SingleReleaseSpecProvider spec = new(new KeyedFrameSpec(), 1, 1);
        string rulesPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.rules");
        await File.WriteAllTextAsync(rulesPath, $"from {(guarded ? Contract : new Address("0x00000000000000000000000000000000000000aa"))}\n");
        try
        {
            using EvmExecutor calls = new(source, spec, NullLogManager.Instance);
            using RpcBackend rpc = new(source, () => source.Head, 1, calls, spec, () => source.Head,
                sendPolicy: new SendPolicyRelay(rulesPath, 1, NullLogManager.Instance));
            Transaction transaction = CreateFrameSubmission();
            source.BlockGasLimit = 30_000_000;
            byte[] encoded = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
            JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { "0x" + Convert.ToHexString(encoded).ToLowerInvariant() });
            if (guarded)
            {
                RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(error!.Message, Does.Contain("frame transaction is not allowed"));
                    Assert.That(source.Broadcasts, Is.Zero);
                }
            }
            else
            {
                object? result = await rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(result, Is.EqualTo(transaction.CalculateHash().ToString()));
                    Assert.That(source.Broadcasts, Is.EqualTo(1));
                }
            }
        }
        finally { File.Delete(rulesPath); }
    }

    [Test]
    public async Task Frame_submission_checks_the_complete_fee_budget_before_fetching_state(
        [Values("execution", "intrinsic", "blob")] string component, [Values(-1, 0, 1)] int extra)
    {
        FakeSource source = new([0x60, 0x03, 0x60, 0x00, 0x60, 0x00, 0xaa, 0x00], contractBalance: 1_000_000_000);
        SingleReleaseSpecProvider specs = new(new KeyedFrameSpec(), 1, 1);
        Transaction transaction = CreateFrameSubmission(data: component == "intrinsic" ? new byte[4096] : default);
        if (component == "blob") await AddFrameBlobAsync(transaction);
        BlockHeader header = await source.GetHeaderAsync(source.Head, CancellationToken.None);
        IReleaseSpec spec = specs.GetSpec(header);
        UInt256 nextBaseFee = BaseFeeCalculator.Calculate(header, specs.GetSpecFor1559(header.Number + 1));
        UInt256 blobFee = UInt256.Zero;
        if (transaction.CarriesBlobs)
        {
            ulong nextExcess = BlobGasCalculator.CalculateExcessBlobGas(header, spec) ?? 0;
            bool pricedBlob = BlobGasCalculator.TryCalculateFeePerBlobGas(nextExcess, spec.BlobBaseFeeUpdateFraction, out UInt256 blobBaseFee);
            Assert.That(pricedBlob, Is.True);
            bool pricedSidecar = BlobGasCalculator.TryCalculateBlobMaxFee(transaction.GetBlobCount(), blobBaseFee, out blobFee);
            Assert.That(pricedSidecar, Is.True);
        }
        bool pricedGas = FrameTxValidation.TryCalculateGasBudget(transaction, spec, out _, out _, out ulong maxGas);
        Assert.That(pricedGas, Is.True);
        bool pricedBudget = FrameTxFeeBudget.TryCalculate(UInt256.MaxValue, transaction.MaxPriorityFeePerGas, maxGas,
            nextBaseFee, blobFee, out UInt256 required, out _);
        Assert.That(pricedBudget, Is.True);
        transaction.MaxFee = extra < 0 ? required - UInt256.One : required + (UInt256)extra;
        byte[] encoded = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { "0x" + Convert.ToHexString(encoded).ToLowerInvariant() });
        using EvmExecutor calls = new(source, specs, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, calls, specs, () => source.Head);

        if (extra < 0)
        {
            RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(error!.Code, Is.EqualTo(-32003));
                Assert.That(error.Message, Does.Contain("max fee is below"));
                Assert.That(source.Broadcasts, Is.Zero);
                Assert.That(source.CodeFetches, Is.Zero);
                Assert.That(source.StorageFetches, Is.Zero);
            }
        }
        else
        {
            object? result = await rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(transaction.CalculateHash().ToString()));
                Assert.That(source.Broadcasts, Is.EqualTo(1));
                Assert.That(source.CodeFetches, Is.EqualTo(1));
                Assert.That(source.StorageFetches, Is.GreaterThan(0));
            }
        }
    }

    private static Transaction CreateFrameSubmission(ulong executionGas = 200_000, ulong stateGas = 200_000, ReadOnlyMemory<byte> data = default)
    {
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Contract,
            NonceKeys = [(UInt256)7],
            Nonce = 42,
            GasPrice = 1,
            MaxFee = 1_000_000,
            BlobVersionedHashes = [],
            FrameSignatures = [],
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, null,
                executionGas, stateGas, UInt256.Zero, data)],
        };
        transaction.GasLimit = FrameTxValidation.TotalGasLimit(transaction.Frames);
        transaction.FrameCalldataStats = FrameTxNonceCalldata.Measure(transaction);
        return transaction;
    }

    private static async Task AddFrameBlobAsync(Transaction transaction)
    {
        await KzgPolynomialCommitments.InitializeAsync(NullLogManager.Instance.GetClassLogger<EvmExecutorTests>());
        IBlobProofsManager proofs = IBlobProofsManager.For(ProofVersion.V1);
        ShardBlobNetworkWrapper wrapper = proofs.AllocateWrapper(new byte[131_072]);
        proofs.ComputeProofsAndCommitments(wrapper);
        transaction.BlobVersionedHashes = proofs.ComputeHashes(wrapper);
        transaction.NetworkWrapper = wrapper;
    }

    private sealed class KeyedFrameSpec() : ReleaseSpecDecorator(Eip8141Prototype.Instance)
    {
        public override bool IsEip8250Enabled => true;
    }

    [Test]
    public async Task Blob_call_and_estimate_run_in_the_local_evm([Values("eth_call", "eth_estimateGas")] string method,
        [Values(null, "0x0", "0x2")] string? blobFee)
    {
        // BLOBHASH(0), MSTORE(0), RETURN(0,32).
        FakeSource source = new([0x60, 0x00, 0x49, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3], contractBalance: 1_000_000_000);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);
        string versionedHash = "0x01" + new string('0', 62);
        Dictionary<string, object> transaction = new()
        {
            ["type"] = "0x3",
            ["from"] = Contract.ToString(),
            ["to"] = Contract.ToString(),
            ["gas"] = "0x186a0",
            ["maxFeePerGas"] = "0x2",
            ["maxPriorityFeePerGas"] = "0x1",
            ["blobVersionedHashes"] = new[] { versionedHash },
        };
        if (blobFee is not null) transaction["maxFeePerBlobGas"] = blobFee;
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { transaction, "finalized" });
        object result = await rpc.InvokeAsync(method, parameters, CancellationToken.None);
        if (method == "eth_call") Assert.That(result, Is.EqualTo(versionedHash));
        else Assert.That(Convert.ToUInt64(((string)result)[2..], 16), Is.InRange(21_000, 100_000));
        Assert.That(source.CodeFetches, Is.EqualTo(1));
    }

    [TestCase("eth_call", false)]
    [TestCase("eth_estimateGas", false)]
    [TestCase("eth_estimateGas", true)]
    public async Task Frame_call_and_estimate_obey_the_active_fork_and_verified_state(string method, bool omitLimits)
    {
        FakeSource source = new([0x60, 0x03, 0x60, 0x00, 0x60, 0x00, 0xaa, 0x00], contractBalance: 1_000_000_000);
        SingleReleaseSpecProvider spec = new(new KeyedFrameSpec(), 1, 1);
        using EvmExecutor call = new(source, spec, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { new
        {
            type = "0x6", from = Contract.ToString(), nonce = "0x2a", nonceKeys = new[] { "0x7" },
            maxFeePerGas = "0x1", maxPriorityFeePerGas = "0x1", maxFeePerBlobGas = "0x0", blobVersionedHashes = Array.Empty<string>(),
            frames = new[] { new { mode = "0x1", flags = "0x3", executionGas = omitLimits ? null : "0x30d40",
                stateGas = omitLimits ? null : "0x30d40", value = "0x0", data = "0x" } },
            signatures = Array.Empty<object>(),
        }, "finalized" });
        object result = await rpc.InvokeAsync(method, parameters, CancellationToken.None);
        if (method == "eth_call") Assert.That(result, Is.EqualTo("0x"));
        else Assert.That(Convert.ToUInt64(((string)result)[2..], 16), Is.InRange(1, 1_000_000));
        Assert.That(source.CodeFetches, Is.EqualTo(method == "eth_call" || omitLimits ? 1 : 0));
    }

    [Test]
    public async Task Call_executes_verified_bytecode_and_reuses_fetched_state()
    {
        // Return the 32-byte word 42.
        FakeSource source = new([0x60, 0x2a, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" });

        object first = await rpc.InvokeAsync("eth_call", parameters, CancellationToken.None);
        int fetched = source.Fetches;
        object second = await rpc.InvokeAsync("eth_call", parameters, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo("0x" + new string('0', 62) + "2a"));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(source.Fetches, Is.EqualTo(fetched));
            Assert.That(source.CodeFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Call_and_estimate_normalize_base_fee_without_changing_the_authenticated_header(
        [Values("eth_call", "eth_estimateGas")] string method,
        [Values("omitted", "legacy-zero", "legacy", "dynamic-zero", "dynamic")] string fees)
    {
        byte expected = fees is "legacy" or "dynamic" ? (byte)7 : (byte)0;
        // Reject an unexpected BASEFEE so estimation also verifies the execution context.
        FakeSource source = new([0x48, 0x60, expected, 0x14, 0x60, 0x0c, 0x57, 0x60, 0x00, 0x60, 0x00, 0xfd,
            0x5b, 0x48, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3], contractBalance: 1_000_000_000)
        { BaseFee = 7 };
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);
        Dictionary<string, string> transaction = new() { ["from"] = Contract.ToString(), ["to"] = Contract.ToString() };
        if (fees.StartsWith("legacy", StringComparison.Ordinal)) transaction["gasPrice"] = fees == "legacy" ? "0x7" : "0x0";
        if (fees.StartsWith("dynamic", StringComparison.Ordinal))
        {
            transaction["type"] = "0x2";
            transaction["maxFeePerGas"] = fees == "dynamic" ? "0x8" : "0x0";
            transaction["maxPriorityFeePerGas"] = fees == "dynamic" ? "0x1" : "0x0";
        }
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { transaction, "finalized" });

        object first = await rpc.InvokeAsync(method, parameters, CancellationToken.None);
        object second = await rpc.InvokeAsync(method, parameters, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            if (method == "eth_call") Assert.That(first, Is.EqualTo("0x" + new string('0', 63) + expected.ToString("x")));
            else Assert.That(Convert.ToUInt64(((string)first)[2..], 16), Is.InRange(21_000, 30_000));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(source.LastHeader?.BaseFeePerGas, Is.EqualTo((UInt256)7));
        }
    }

    [Test]
    public async Task Execution_reuses_only_unchanged_authenticated_commitments(
        [Values("eth_call", "eth_estimateGas")] string method,
        [Values("world", "storage", "empty-storage", "code", "deleted")] string change)
    {
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" });

        object first = await rpc.InvokeAsync(method, parameters, CancellationToken.None);
        source.Head = new(source.Head.Slot + 1, source.Head.Number + 1, Keccak.Compute("new block"), Keccak.Compute("new state"));
        if (change == "storage")
        {
            source.ContractAccount = source.ContractAccount.WithChangedStorageRoot(Keccak.Compute("new storage"));
            source.StorageResponse = (_, _) => Task.FromResult((UInt256)84);
        }
        else if (change == "empty-storage") source.ContractAccount = source.ContractAccount.WithChangedStorageRoot(Keccak.EmptyTreeHash);
        else if (change == "code")
        {
            source.ContractCode = [0x60, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3];
            source.ContractAccount = source.ContractAccount.WithChangedCodeHash(Keccak.Compute(source.ContractCode));
        }
        else if (change == "deleted") source.ContractAccount = Account.TotallyEmpty;
        object second = await rpc.InvokeAsync(method, parameters, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            if (change == "world") Assert.That(second, Is.EqualTo(first));
            else if (method == "eth_call")
                Assert.That(second, Is.EqualTo(change switch
                {
                    "deleted" => "0x",
                    "empty-storage" => "0x" + new string('0', 64),
                    _ => "0x" + new string('0', 62) + "54",
                }));
            else Assert.That(Convert.ToUInt64(((string)second)[2..], 16), Is.InRange(21_000, 30_000));
            Assert.That(source.CodeFetches, Is.EqualTo(change == "code" ? 2 : 1));
            Assert.That(source.StorageFetches, Is.EqualTo(change == "storage" ? 2 : 1));
            Assert.That(source.ContractAccountFetches, Is.EqualTo(2));
            Assert.That(source.LastQueriedHead, Is.EqualTo(source.Head));
        }
    }

    [Test]
    public async Task Cached_commitments_do_not_skip_account_authentication_at_a_new_root(
        [Values("eth_call", "eth_estimateGas")] string method)
    {
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" });
        await rpc.InvokeAsync(method, parameters, CancellationToken.None);
        source.Head = new(source.Head.Slot + 1, source.Head.Number + 1, Keccak.Compute("unproven block"), Keccak.Compute("unproven state"));
        source.ContractAccountResponse = _ => throw new InvalidDataException("Account proof failed.");

        Assert.That(async () => await rpc.InvokeAsync(method, parameters, CancellationToken.None),
            Throws.TypeOf<RpcException>().With.Property(nameof(RpcException.Code)).EqualTo(-32000));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.ContractAccountFetches, Is.EqualTo(2));
            Assert.That(source.StorageFetches, Is.EqualTo(1));
            Assert.That(source.CodeFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Concurrent_hydration_keeps_heads_and_cancellation_isolated([Values] bool cancelFirst)
    {
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        VerifiedHead firstHead = source.Head;
        VerifiedHead secondHead = new(firstHead.Slot + 1, firstHead.Number + 1, Keccak.Compute("parallel block"), Keccak.Compute("parallel state"));
        source.ContractAccountResponse = head => source.ContractAccount.WithChangedStorageRoot(head.StateRoot);
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.StorageResponse = async (head, token) =>
        {
            if (head == firstHead) firstEntered.TrySetResult();
            else if (head == secondHead) secondEntered.TrySetResult();
            else throw new AssertionException("Unexpected authenticated head.");
            await release.Task.WaitAsync(token);
            return (UInt256)(head == firstHead ? 42 : 84);
        };
        using EvmExecutor calls = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource interrupted = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        JsonElement transaction = JsonSerializer.SerializeToElement(new { to = Contract.ToString() });

        Task<string> first = calls.ExecuteAsync(firstHead, transaction, false, interrupted.Token);
        Task<string> second = calls.ExecuteAsync(secondHead, transaction, false, timeout.Token);
        try
        {
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(timeout.Token);
            if (cancelFirst)
            {
                interrupted.Cancel();
                await Assert.CatchAsync<OperationCanceledException>(async () => await first);
                Assert.That(second.IsCompleted, Is.False);
            }
        }
        finally { release.TrySetResult(); }

        string firstResult = cancelFirst
            ? await calls.ExecuteAsync(firstHead, transaction, false, timeout.Token)
            : await first;
        string secondResult = await second;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult, Is.EqualTo("0x" + new string('0', 62) + "2a"));
            Assert.That(secondResult, Is.EqualTo("0x" + new string('0', 62) + "54"));
        }
    }

    [Test]
    public async Task Nested_call_and_estimate_keep_request_state_isolated([Values("eth_call", "eth_estimateGas")] string method)
    {
        // The outer call invokes itself with one calldata byte; the inner call increments authenticated storage.
        FakeSource source = new([0x36, 0x15, 0x60, 0x17, 0x57,
            0x60, 0x00, 0x54, 0x60, 0x01, 0x01, 0x80, 0x60, 0x00, 0x55,
            0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3, 0x5b,
            0x60, 0x20, 0x60, 0x00, 0x60, 0x01, 0x60, 0x00, 0x60, 0x00, 0x30, 0x5a, 0xf1,
            0x60, 0x2d, 0x57, 0x60, 0x00, 0x60, 0x00, 0xfd, 0x5b,
            0x60, 0x20, 0x60, 0x00, 0xf3]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" });

        object first = await rpc.InvokeAsync(method, parameters, CancellationToken.None);
        object second = await rpc.InvokeAsync(method, parameters, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            if (method == "eth_call") Assert.That(first, Is.EqualTo("0x" + new string('0', 62) + "2b"));
            else Assert.That(Convert.ToUInt64(((string)first)[2..], 16), Is.InRange(21_000, 100_000));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(source.CodeFetches, Is.EqualTo(1));
            Assert.That(source.StorageFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Storage_batches_clear_and_write_without_accessing_the_shared_address_map()
    {
        FakeSource source = new([]);
        using BoundedMemoryCache cache = new();
        Type callStateType = typeof(EvmExecutor).GetNestedType("CallState", BindingFlags.NonPublic)!;
        IWorldStateScopeProvider state = (IWorldStateScopeProvider)Activator.CreateInstance(callStateType, source.Head, cache)!;
        BlockHeader header = await source.GetHeaderAsync(source.Head, CancellationToken.None);
        Assert.That(state.TryBeginScope(header, new LocalMetrics(), out IWorldStateScopeProvider.IScope? opened), Is.True);
        using IWorldStateScopeProvider.IScope scope = opened!;

        // The root committer creates the batches on its owning thread before giving each batch to a worker.
        bool workersApplying = false;
        IEqualityComparer<Address> comparer = EqualityComparer<Address>.Create(
            static (left, right) => left == right,
            address => !workersApplying ? address.GetHashCode() : throw new InvalidOperationException("Storage worker used the shared address map."));
        FieldInfo map = scope.GetType().GetField("_writtenStorage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        map.SetValue(scope, Activator.CreateInstance(map.FieldType, comparer));
        Address updated = Address.Zero;
        Account account = source.ContractAccount;
        cache.Set((source.Head.StateRoot, updated), account, 512);
        cache.Set((account.StorageRoot, updated, UInt256.Zero), (UInt256)42, 512);

        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(2);
        using IWorldStateScopeProvider.IStorageWriteBatch cleared = batch.CreateStorageWriteBatch(Contract, 1);
        using IWorldStateScopeProvider.IStorageWriteBatch written = batch.CreateStorageWriteBatch(updated, 1);
        cleared.Set(UInt256.Zero, 9);
        workersApplying = true;
        await Task.WhenAll(Task.Run(() =>
        {
            cleared.Clear();
            cleared.Set(UInt256.One, 7);
        }), Task.Run(() => written.Set(UInt256.One, 8)));
        workersApplying = false;

        IWorldStateScopeProvider.IStorageTree clearedTree = scope.CreateStorageTree(Contract);
        IWorldStateScopeProvider.IStorageTree updatedTree = scope.CreateStorageTree(updated);
        clearedTree.Get(UInt256.Zero, out UInt256 clearedValue);
        clearedTree.Get(UInt256.One, out UInt256 clearedWrite);
        updatedTree.Get(UInt256.Zero, out UInt256 authenticatedValue);
        updatedTree.Get(UInt256.One, out UInt256 updatedWrite);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clearedValue, Is.EqualTo(UInt256.Zero));
            Assert.That(clearedWrite, Is.EqualTo((UInt256)7));
            Assert.That(authenticatedValue, Is.EqualTo((UInt256)42));
            Assert.That(updatedWrite, Is.EqualTo((UInt256)8));
        }
    }

    [Test]
    public async Task Call_fetches_storage_touched_by_evm()
    {
        // SLOAD(0), MSTORE(0), RETURN(0, 32).
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);

        object result = await rpc.InvokeAsync("eth_call", JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" }), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo("0x" + new string('0', 62) + "2a"));
            Assert.That(source.StorageFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Call_writes_are_discarded_between_requests()
    {
        // Return SLOAD(0) + 1 after writing it; the next call must still read the finalized 42.
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x01, 0x01, 0x80, 0x60, 0x00, 0x55,
            0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" });

        object first = await rpc.InvokeAsync("eth_call", parameters, CancellationToken.None);
        object second = await rpc.InvokeAsync("eth_call", parameters, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo("0x" + new string('0', 62) + "2b"));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(source.StorageFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Estimate_uses_verified_state_and_reuses_fetched_proofs()
    {
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" });

        object first = await rpc.InvokeAsync("eth_estimateGas", parameters, CancellationToken.None);
        int fetched = source.Fetches;
        object second = await rpc.InvokeAsync("eth_estimateGas", parameters, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Convert.ToUInt64(((string)first)[2..], 16), Is.InRange(21_000, 30_000));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(source.Fetches, Is.EqualTo(fetched));
            Assert.That(source.StorageFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Revert_is_an_error_with_return_data([Values("eth_call", "eth_estimateGas")] string method,
        [Values("empty", "reason", "raw")] string payload)
    {
        byte[] code = payload switch
        {
            "reason" => Convert.FromHexString("6308c379a060e01b60005260206004526001602452607860f81b60445260646000fd"),
            "raw" => [0x60, 0x42, 0x60, 0x00, 0x53, 0x60, 0x01, 0x60, 0x00, 0xfd],
            _ => [0x60, 0x00, 0x60, 0x00, 0xfd],
        };
        FakeSource source = new(code);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync(method,
            JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" }), CancellationToken.None));

        string data = payload switch
        {
            "reason" => "0x08c379a0" + new string('0', 62) + "20" + new string('0', 63) + "1" + "78" + new string('0', 62),
            "raw" => "0x42",
            _ => "0x",
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error!.Code, Is.EqualTo(3));
            Assert.That(error.Message, Is.EqualTo(payload == "reason" ? "execution reverted: x" : "execution reverted"));
            Assert.That(error.RevertData, Is.EqualTo(data));
        }
    }

    [Test]
    public async Task Blockhash_uses_verified_ancestry_or_state_for_the_active_fork([Values] bool fromState)
    {
        // PUSH4(26,000,000 - 2), BLOCKHASH, MSTORE, RETURN.
        FakeSource source = new([0x63, 0x01, 0x8c, 0xba, 0x7e, 0x40, 0x60, 0x00, 0x52,
            0x60, 0x20, 0x60, 0x00, 0xf3]);
        ISpecProvider specs = fromState ? new SingleReleaseSpecProvider(new StateBlockhashSpec(), 1, 1) : MainnetSpecProvider.Instance;
        using EvmExecutor call = new(source, specs, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);

        object result = await rpc.InvokeAsync("eth_call", JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" }), CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(result, Is.EqualTo(fromState ? "0x" + new string('0', 62) + "2a" : Keccak.Compute("grandparent").ToString()));
        Assert.That(source.AncestorFetches, Is.EqualTo(fromState ? 0 : 1));
        Assert.That(source.StorageFetches, Is.EqualTo(fromState ? 1 : 0));
        if (fromState)
        {
            Assert.That(source.LastStorageAddress, Is.EqualTo(Eip2935Constants.BlockHashHistoryAddress));
            Assert.That(source.LastStorageKey, Is.EqualTo(new UInt256((source.Head.Number - 2) % Eip2935Constants.RingBufferSize)));
        }
    }

    private sealed class StateBlockhashSpec() : ReleaseSpecDecorator(Prague.Instance)
    {
        public override bool IsEip7709Enabled => true;
    }

    [Test]
    public async Task Blockhash_reads_reuse_one_complete_ancestor_window([Values] bool newestFirst)
    {
        byte[] code = Convert.FromHexString(newestFirst
            ? "600060015b8043034090911890600101806100c9116004575060005260206000f3"
            : "600060c85b804303409091189060019003806004575060005260206000f3");
        FakeSource source = new(code);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        byte[] expected = new byte[32];
        for (ulong depth = 1; depth <= 200; depth++)
        {
            ReadOnlySpan<byte> hash = FakeSource.HashAt(source.Head, source.Head.Number - depth).Bytes;
            for (int i = 0; i < expected.Length; i++) expected[i] ^= hash[i];
        }

        string result = await call.ExecuteAsync(source.Head,
            JsonSerializer.SerializeToElement(new { to = Contract.ToString() }), false, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(new Hash256(expected).ToString()));
            Assert.That(source.AncestorFetches, Is.EqualTo(1));
            Assert.That(source.FirstAncestorNumber, Is.LessThanOrEqualTo(source.Head.Number - 200));
        }
    }

    [Test]
    public async Task Blockhash_bounds_and_parent_shortcut_are_preserved([Values(1, 256, 257)] int depth)
    {
        FakeSource source = new([0x43, 0x61, (byte)(depth >> 8), (byte)depth, 0x90, 0x03, 0x40,
            0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);

        string result = await call.ExecuteAsync(source.Head,
            JsonSerializer.SerializeToElement(new { to = Contract.ToString() }), false, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(depth > 256 ? Hash256.Zero.ToString()
                : FakeSource.HashAt(source.Head, source.Head.Number - (ulong)depth).ToString()));
            Assert.That(source.AncestorFetches, Is.EqualTo(depth == 256 ? 1 : 0));
        }
    }

    [Test]
    public async Task Blockhash_cache_is_bound_to_the_authenticated_branch()
    {
        FakeSource source = new([0x43, 0x60, 0x02, 0x90, 0x03, 0x40, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        JsonElement transaction = JsonSerializer.SerializeToElement(new { to = Contract.ToString() });
        VerifiedHead first = source.Head;
        string original = await call.ExecuteAsync(first, transaction, false, CancellationToken.None);
        source.Head = source.Head with { BlockHash = Keccak.Compute("alternate block"), StateRoot = Keccak.Compute("alternate state") };
        string alternate = await call.ExecuteAsync(source.Head, transaction, false, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(original, Is.EqualTo(FakeSource.HashAt(first, first.Number - 2).ToString()));
            Assert.That(alternate, Is.EqualTo(FakeSource.HashAt(source.Head, source.Head.Number - 2).ToString()));
            Assert.That(alternate, Is.Not.EqualTo(original));
            Assert.That(source.AncestorFetches, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task Second_contract_executes_its_own_authenticated_code()
    {
        Address second = new("0x2234567890123456789012345678901234567890");
        FakeSource source = new([0x60, 0x20, 0x60, 0x00, 0x60, 0x00, 0x60, 0x00, 0x73, .. second.Bytes,
            0x5a, 0xfa, 0x50, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        source.ExtraContracts.Add(second, [0x60, 0x2a, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);

        string result = await call.ExecuteAsync(source.Head,
            JsonSerializer.SerializeToElement(new { to = Contract.ToString() }), false, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo("0x" + new string('0', 62) + "2a"));
            Assert.That(source.CodeFetches, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task Execution_gas_is_capped_even_when_the_request_asks_for_more()
    {
        FakeSource source = new([0x5a, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        string result = await call.ExecuteAsync(source.Head,
            JsonSerializer.SerializeToElement(new { to = Contract.ToString(), gas = "0xe4e1c0" }), false, CancellationToken.None);
        Assert.That(UInt256.Parse(result), Is.InRange((UInt256)9_900_000, (UInt256)10_000_000));
    }

    [Test]
    public async Task Execution_bounds_distinct_state_fill_rounds([Values(123, 130)] int accounts)
    {
        byte[] code = new byte[accounts * 5 + 1];
        for (int i = 0; i < accounts; i++)
        {
            code[i * 5] = 0x61;
            code[i * 5 + 2] = (byte)(i + 1);
            code[i * 5 + 3] = 0x31;
            code[i * 5 + 4] = 0x50;
        }
        FakeSource source = new(code);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        JsonElement transaction = JsonSerializer.SerializeToElement(new { to = Contract.ToString() });
        if (accounts == 123)
            Assert.That(await call.ExecuteAsync(source.Head, transaction, false, CancellationToken.None), Is.EqualTo("0x"));
        else
        {
            RpcException? error = await Assert.ThrowsAsync<RpcException>(async () =>
                await call.ExecuteAsync(source.Head, transaction, false, CancellationToken.None));
            Assert.That(error!.Message, Does.Contain("too many distinct state items"));
        }
    }

    [Test]
    public void Verified_cache_admits_large_entries_after_warming_and_evicts_least_recently_used()
    {
        using BoundedMemoryCache cache = new();
        for (int i = 0; i < 32; i++) cache.Set(i, i, BoundedMemoryCache.Capacity / 32);
        bool foundFirst = cache.TryGetValue(0, out int first);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundFirst, Is.True);
            Assert.That(first, Is.Zero);
        }
        cache.Set("large", 42, BoundedMemoryCache.Capacity / 2);
        bool foundRetained = cache.TryGetValue(0, out int retained);
        bool foundEvicted = cache.TryGetValue(1, out int _);
        bool foundLarge = cache.TryGetValue("large", out int large);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundRetained, Is.True);
            Assert.That(retained, Is.Zero);
            Assert.That(foundEvicted, Is.False);
            Assert.That(foundLarge, Is.True);
            Assert.That(large, Is.EqualTo(42));
        }
        cache.Set("large", 43, BoundedMemoryCache.Capacity);
        cache.Set("oversized", 99, BoundedMemoryCache.Capacity + 1);
        bool foundOld = cache.TryGetValue(0, out int _);
        bool foundReplaced = cache.TryGetValue("large", out int replaced);
        bool foundOversized = cache.TryGetValue("oversized", out int _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundOld, Is.False);
            Assert.That(foundReplaced, Is.True);
            Assert.That(replaced, Is.EqualTo(43));
            Assert.That(foundOversized, Is.False);
        }
    }

    [Test]
    public async Task Network_fills_do_not_hold_execution_permits_or_starve_a_cached_call()
    {
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        Address warm = new("0x3234567890123456789012345678901234567890");
        source.ExtraContracts.Add(warm, [0x60, 0x2a, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        JsonElement warmRequest = JsonSerializer.SerializeToElement(new { to = warm.ToString() });
        string expected = await call.ExecuteAsync(source.Head, warmRequest, false, CancellationToken.None);
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int fills = 0;
        source.StorageResponse = async (_, token) =>
        {
            if (Interlocked.Increment(ref fills) == 4) waiting.TrySetResult();
            await release.Task.WaitAsync(token);
            return (UInt256)42;
        };
        JsonElement coldRequest = JsonSerializer.SerializeToElement(new { to = Contract.ToString() });
        Task<string>[] slow = Enumerable.Range(0, 4)
            .Select(_ => call.ExecuteAsync(source.Head, coldRequest, false, CancellationToken.None)).ToArray();
        Task<string>? cached = null;
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cached = call.ExecuteAsync(source.Head, warmRequest, false, CancellationToken.None);
            string completed = await cached.WaitAsync(TimeSpan.FromSeconds(5));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(completed, Is.EqualTo(expected));
                Assert.That(slow, Has.All.Property(nameof(Task.IsCompleted)).EqualTo(false));
            }
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(slow);
            if (cached is not null) await cached;
        }
    }

    [Test]
    public async Task Execution_admission_and_network_wait_have_a_local_deadline(
        [Values("admission", "fill")] string stage, [Values] bool callerCancels)
    {
        FakeSource source = new(stage == "fill" ? [0x60, 0x00, 0x54, 0x00] : [0x00]);
        source.StorageResponse = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return UInt256.Zero;
        };
        CallClock clock = new();
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance, clock);
        SemaphoreSlim slots = (SemaphoreSlim)typeof(EvmExecutor).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(call)!;
        using CancellationTokenSource caller = new();
        int held = 0;
        Task<string>? request = null;
        try
        {
            if (stage == "admission")
            {
                for (; held < 4; held++) Assert.That(slots.Wait(0), Is.True);
                Assert.That(slots.Wait(0), Is.False);
            }
            request = call.ExecuteAsync(source.Head,
                JsonSerializer.SerializeToElement(new { to = Contract.ToString() }), false, caller.Token);
            Assert.That(request.IsCompleted, Is.False);
            if (stage == "fill") Assert.That(source.StorageFetches, Is.EqualTo(1));
            Assert.That(clock.DueTime, Is.EqualTo(TimeSpan.FromSeconds(60)));
            if (callerCancels)
            {
                caller.Cancel();
                Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
            }
            else
            {
                clock.Expire();
                RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await request);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(error!.Code, Is.EqualTo(-32002));
                    Assert.That(error.Message, Is.EqualTo("Execution request timed out."));
                }
            }
        }
        finally
        {
            caller.Cancel();
            if (request is not null) await ((Task)request).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (held != 0) slots.Release(held);
        }
        Assert.That(slots.CurrentCount, Is.EqualTo(4));
    }

    private sealed class CallClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        internal TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return new Timer();
        }
        internal void Expire() => _callback!(_state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }

    [Test]
    public async Task Header_must_match_the_authenticated_snapshot([Values("hash", "number", "state")] string corruption)
    {
        FakeSource source = new([0x00]) { HeaderCorruption = corruption };
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_call",
            JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" }), CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error!.Code, Is.EqualTo(-32000));
            Assert.That(source.Fetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Execution_rejects_unverified_selector_before_fetch([Values("eth_call", "eth_estimateGas")] string method,
        [Values("latest", "pending")] string selector)
    {
        FakeSource source = new([0x00]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call);

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync(method,
            JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, selector }), CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error!.Code, Is.EqualTo(-32001));
            Assert.That(source.Fetches, Is.Zero);
        }
    }

    [Test]
    public async Task Execution_defaults_missing_selector_to_latest([Values("eth_call", "eth_estimateGas")] string method)
    {
        FakeSource source = new([0x00]);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => throw new AssertionException("The finalized head must not be selected."),
            1, call, getLatestHead: () => source.Head);

        object result = await rpc.InvokeAsync(method,
            JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() } }), CancellationToken.None);

        if (method == "eth_call") Assert.That(result, Is.EqualTo("0x"));
        else Assert.That(Convert.ToUInt64(((string)result)[2..], 16), Is.InRange(21_000, 30_000));
        Assert.That(source.Fetches, Is.GreaterThan(0));
    }

    [Test]
    public async Task Execution_keeps_a_pinned_optimistic_snapshot_when_native_head_advances(
        [Values("eth_call", "eth_estimateGas")] string method, [Values(0, 1, 2, 3)] int selectorKind)
    {
        FakeSource source = new([0x60, 0x2a, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        VerifiedHead finalized = new(98, source.Head.Number - 64, Keccak.Compute("finalized ancestor"), source.Head.StateRoot);
        VerifiedHead latest = new(101, source.Head.Number + 1, Keccak.Compute("advanced optimistic head"), source.Head.StateRoot);
        object selector = selectorKind switch
        {
            0 => source.Head.BlockHash.ToString(),
            1 => $"0x{source.Head.Number:x}",
            2 => new { blockHash = source.Head.BlockHash.ToString(), requireCanonical = true },
            _ => new { blockNumber = $"0x{source.Head.Number:x}" },
        };
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => finalized, 1, call, getLatestHead: () => latest);

        object result = await rpc.InvokeAsync(method, JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, selector }), CancellationToken.None);

        if (method == "eth_call") Assert.That(result, Is.EqualTo("0x" + new string('0', 62) + "2a"));
        else Assert.That(Convert.ToUInt64(((string)result)[2..], 16), Is.InRange(21_000, 30_000));
        Assert.That(source.LastQueriedHead, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.LastCanonicalAnchor, Is.EqualTo(latest));
            Assert.That(source.LastQueriedHead!.BlockHash, Is.EqualTo(source.Head.BlockHash));
        }
        source.CanonicalHistoricalHead = new(source.Head.Slot, source.Head.Number, Keccak.Compute("replacement optimistic ancestor"), source.Head.StateRoot);
        RpcException? orphan = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync(method,
            JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, new { blockHash = source.Head.BlockHash.ToString(), requireCanonical = true } }), CancellationToken.None));
        Assert.That(orphan!.Code, Is.EqualTo(-32000));
    }

    [Test]
    public async Task Call_phase_diagnostics_are_opt_in_and_keep_the_selected_head([Values] bool debug, [Values] bool blockhash)
    {
        byte[] code = blockhash
            ? [0x63, 0x01, 0x8c, 0xba, 0x7e, 0x40, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]
            : [0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3];
        FakeSource source = new(code);
        ConcurrentQueue<string> entries = new();
        ILogManager logs = DiagnosticLogs(debug, entries);
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, logs);
        using Activity parent = new Activity("test-wallet-request").SetIdFormat(ActivityIdFormat.W3C).Start();
        Activity? observed = null;
        source.ContractAccountResponse = _ =>
        {
            observed = Activity.Current;
            return source.ContractAccount;
        };

        string result = await call.ExecuteAsync(source.Head,
            JsonSerializer.SerializeToElement(new { to = Contract.ToString() }), false, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(blockhash ? Keccak.Compute("grandparent").ToString() : "0x" + new string('0', 62) + "2a"));
            Assert.That(Activity.Current, Is.SameAs(parent));
        }
        if (!debug)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(entries, Is.Empty);
                Assert.That(observed, Is.SameAs(parent));
            }
            return;
        }
        Assert.That(observed, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(observed!.OperationName, Is.EqualTo("LightClientCall"));
            Assert.That(observed!.ParentId, Is.EqualTo(parent.Id));
            Assert.That(entries.Any(entry => entry.Contains($"block={source.Head.BlockHash} number={source.Head.Number} stateRoot={source.Head.StateRoot}")), Is.True);
            foreach (string kind in new[] { "Account", "Code", blockhash ? "Blockhash" : "Storage" })
                Assert.That(entries.Any(entry => entry.Contains($"phase=fill kind={kind} count=1 outcome=filled")), Is.True);
            Assert.That(entries.Any(entry => entry.Contains("phase=execution") && entry.Contains("outcome=completed")), Is.True);
        }

        int fetches = source.Fetches;
        int fills = entries.Count(entry => entry.Contains("phase=fill"));
        string cachedResult = await call.ExecuteAsync(source.Head,
            JsonSerializer.SerializeToElement(new { to = Contract.ToString() }), false, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cachedResult, Is.EqualTo(result));
            Assert.That(source.Fetches, Is.EqualTo(fetches));
            Assert.That(entries.Count(entry => entry.Contains("phase=fill")), Is.EqualTo(fills));
            Assert.That(entries.Any(entry => entry.Contains("phase=header source=cache")), Is.True);
        }
    }

    [Test]
    public async Task Concurrent_call_diagnostics_do_not_merge_and_execution_stops_before_fill_wait()
    {
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        ConcurrentQueue<string> entries = new();
        ConcurrentQueue<string?> callers = new();
        TaskCompletionSource bothWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.StorageResponse = async (_, token) =>
        {
            callers.Enqueue(Activity.Current?.Id);
            if (callers.Count == 2) bothWaiting.TrySetResult();
            await release.Task.WaitAsync(token);
            return (UInt256)42;
        };
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, DiagnosticLogs(true, entries));
        using Activity parent = new Activity("test-wallet-request").SetIdFormat(ActivityIdFormat.W3C).Start();
        JsonElement transaction = JsonSerializer.SerializeToElement(new { to = Contract.ToString() });
        Task<string>[] requests = [call.ExecuteAsync(source.Head, transaction, false, CancellationToken.None),
            call.ExecuteAsync(source.Head, transaction, false, CancellationToken.None)];
        try
        {
            await bothWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(callers.Distinct().Count(), Is.EqualTo(2));
                foreach (string? id in callers)
                    Assert.That(entries.Any(entry => entry.Contains($"id={id} phase=execution") && entry.Contains("outcome=missing kind=Storage")), Is.True);
                Assert.That(entries.Any(entry => entry.Contains("phase=fill kind=Storage")), Is.False);
            }
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(requests);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Count(entry => entry.Contains("phase=fill kind=Storage count=1 outcome=filled")), Is.EqualTo(2));
            Assert.That(Activity.Current, Is.SameAs(parent));
        }
    }

    [Test]
    public async Task Failed_fill_is_not_reported_as_success_and_releases_the_call_scope()
    {
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        ConcurrentQueue<string> entries = new();
        source.StorageResponse = (_, _) => Task.FromException<UInt256>(new InvalidDataException("test state failure"));
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, DiagnosticLogs(true, entries));
        using Activity parent = new Activity("test-wallet-request").SetIdFormat(ActivityIdFormat.W3C).Start();
        JsonElement transaction = JsonSerializer.SerializeToElement(new { to = Contract.ToString() });

        await Assert.ThrowsAsync<InvalidDataException>(async () => await call.ExecuteAsync(source.Head, transaction, false, CancellationToken.None));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Activity.Current, Is.SameAs(parent));
            Assert.That(entries.Any(entry => entry.Contains("phase=fill kind=Storage count=1 outcome=failed")), Is.True);
            Assert.That(entries.Any(entry => entry.Contains("phase=fill kind=Storage count=1 outcome=filled")), Is.False);
            Assert.That(entries.Any(entry => entry.Contains("phase=execution") && entry.Contains("outcome=completed")), Is.False);
        }

        source.StorageResponse = null;
        string result = await call.ExecuteAsync(source.Head, transaction, false, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo("0x" + new string('0', 62) + "2a"));
            Assert.That(Activity.Current, Is.SameAs(parent));
        }
    }

    [Test]
    public async Task Enabling_debug_during_a_call_does_not_invent_started_intervals()
    {
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        ConcurrentQueue<string> entries = new();
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Activity? observed = null;
        source.StorageResponse = async (_, token) =>
        {
            observed = Activity.Current;
            waiting.TrySetResult();
            await release.Task.WaitAsync(token);
            return (UInt256)42;
        };
        using EvmExecutor call = new(source, MainnetSpecProvider.Instance, DiagnosticLogs(false, entries));
        using Activity parent = new Activity("test-wallet-request").SetIdFormat(ActivityIdFormat.W3C).Start();
        JsonElement transaction = JsonSerializer.SerializeToElement(new { to = Contract.ToString() });
        Task<string> request = call.ExecuteAsync(source.Head, transaction, false, CancellationToken.None);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            FieldInfo logger = typeof(EvmExecutor).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!;
            logger.SetValue(call, DiagnosticLogs(true, entries).GetLogger(EvmExecutor.LogCategory));
            Assert.That(((Nethermind.Logging.ILogger)logger.GetValue(call)!).IsDebug, Is.True);
        }
        finally
        {
            release.TrySetResult();
            await request;
        }
        string result = await request;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo("0x" + new string('0', 62) + "2a"));
            Assert.That(entries, Is.Empty);
            Assert.That(observed, Is.SameAs(parent));
            Assert.That(Activity.Current, Is.SameAs(parent));
        }

        await call.ExecuteAsync(source.Head, transaction, false, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Any(entry => entry.Contains("operation=call")), Is.True);
            Assert.That(entries.Any(entry => entry.Contains("phase=completion outcome=completed")), Is.True);
            Assert.That(Activity.Current, Is.SameAs(parent));
        }
    }

    [Test]
    public async Task Simulate_chains_state_and_block_hashes_across_simulated_blocks(
        [Values("shanghai", "prague", "amsterdam")] string fork, [Values] bool historical)
    {
        // An empty call returns slot 0; any other call stores its first word there.
        byte[] code = [0x36, 0x15, 0x60, 0x0c, 0x57, 0x60, 0x00, 0x35, 0x60, 0x00, 0x55, 0x00, 0x5b, 0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3];
        // Returns BLOCKHASH(NUMBER - 2).
        const string blockhashReader = "0x43600290034060005260206000f3";
        Address sender = new("0x00000000000000000000000000000000000000c1");
        Address recipient = new("0x00000000000000000000000000000000000000c2");
        Address reader = new("0x00000000000000000000000000000000000000b0");
        FakeSource source = new(code);
        if (fork != "shanghai")
        {
            source.ExtraContracts[Eip7002Constants.WithdrawalRequestPredeployAddress] = [0x00];
            source.ExtraContracts[Eip7251Constants.ConsolidationRequestPredeployAddress] = [0x00];
        }
        if (fork == "amsterdam")
        {
            source.ExtraContracts[Eip8282Constants.BuilderDepositRequestPredeployAddress] = [0x00];
            source.ExtraContracts[Eip8282Constants.BuilderExitRequestPredeployAddress] = [0x00];
        }
        SingleReleaseSpecProvider spec = new(fork switch { "prague" => Prague.Instance, "amsterdam" => Amsterdam.Instance, _ => Shanghai.Instance }, 1, 1);
        VerifiedHead baseHead = source.Head;
        VerifiedHead finalized = historical
            ? new(98, baseHead.Number - 64, Keccak.Compute("finalized ancestor"), Keccak.Compute("finalized state"))
            : baseHead;
        VerifiedHead latest = historical
            ? new(101, baseHead.Number + 1, Keccak.Compute("advanced optimistic head"), Keccak.Compute("optimistic state"))
            : baseHead;
        using EvmExecutor call = new(source, spec, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => finalized, 1, call, spec, () => latest);
        object payload = new
        {
            blockStateCalls = new object[]
            {
                new
                {
                    stateOverrides = new Dictionary<string, object>
                    {
                        [sender.ToString()] = new { balance = "0xde0b6b3a7640000" },
                        [reader.ToString()] = new { code = blockhashReader },
                    },
                    calls = new object[]
                    {
                        new { from = sender.ToString(), to = Contract.ToString(), input = "0x" + new string('0', 63) + "7" },
                        new { from = sender.ToString(), to = recipient.ToString(), value = "0x5" },
                    },
                },
                new { calls = new object[] { new { from = sender.ToString(), to = Contract.ToString() }, new { from = sender.ToString(), to = reader.ToString() } } },
                new { calls = new object[] { new { from = sender.ToString(), to = reader.ToString() } } },
            },
            traceTransfers = true,
        };

        object[] parameters = historical ? [payload, new { blockNumber = RpcBackend.Quantity(baseHead.Number) }] : [payload];
        JsonElement blocks = (JsonElement)await rpc.InvokeAsync("eth_simulateV1", JsonSerializer.SerializeToElement(parameters), CancellationToken.None);

        string Word(ulong value) => "0x" + value.ToString("x64");
        string Hash(int block) => blocks[block].GetProperty("hash").GetString()!;
        JsonElement Call(int block, int index) => blocks[block].GetProperty("calls")[index];
        Assert.That(blocks.GetArrayLength(), Is.EqualTo(3));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.LastCanonicalAnchor, Is.EqualTo(historical ? latest : null));
            Assert.That(blocks[0].GetProperty("number").GetString(), Is.EqualTo(RpcBackend.Quantity(baseHead.Number + 1)));
            Assert.That(blocks[0].GetProperty("parentHash").GetString(), Is.EqualTo(baseHead.BlockHash.ToString()));
            Assert.That(blocks[0].GetProperty("stateRoot").GetString(), Is.EqualTo(baseHead.StateRoot.ToString()));
            Assert.That(blocks[1].GetProperty("parentHash").GetString(), Is.EqualTo(Hash(0)));
            Assert.That(blocks[2].GetProperty("parentHash").GetString(), Is.EqualTo(Hash(1)));
            Assert.That(Call(0, 0).GetProperty("status").GetString(), Is.EqualTo("0x1"));
            // From EIP-7708 the EVM logs value transfers itself, from the system address, instead of the simulator.
            Assert.That(Call(0, 1).GetProperty("logs")[0].GetProperty("address").GetString(),
                Is.EqualTo((fork == "amsterdam" ? TransferLog.Sender : TransferLog.Erc20Sender).ToString()));
            Assert.That(Call(0, 1).GetProperty("logs")[0].GetProperty("blockHash").GetString(), Is.EqualTo(Hash(0)));
            // The fake source serves 42 for every authenticated slot, so 7 can only come from the first block's write.
            Assert.That(Call(1, 0).GetProperty("returnData").GetString(), Is.EqualTo(Word(7)));
            Assert.That(Call(1, 1).GetProperty("returnData").GetString(), Is.EqualTo(baseHead.BlockHash.ToString()));
            Assert.That(Call(2, 0).GetProperty("returnData").GetString(), Is.EqualTo(Hash(0)));
            // From Prague the request contracts run their shared code at the end of each block.
            Assert.That(source.CodeFetches, Is.EqualTo(fork == "shanghai" ? 1 : 2));
        }
    }

    [TestCase("empty", ErrorCodes.InvalidParams)]
    [TestCase("null-block", ErrorCodes.InvalidParams)]
    [TestCase("null-call", ErrorCodes.InvalidParams)]
    [TestCase("too-many-blocks", ErrorCodes.ClientLimitExceededError)]
    [TestCase("blocks-out-of-order", ErrorCodes.InvalidInputBlocksOutOfOrder)]
    [TestCase("malformed", ErrorCodes.InvalidParams)]
    public async Task Simulate_rejects_invalid_requests_with_the_simulate_error_codes(string scenario, int expectedCode)
    {
        FakeSource source = new([0x00]);
        SingleReleaseSpecProvider spec = new(Shanghai.Instance, 1, 1);
        using EvmExecutor call = new(source, spec, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, call, spec, () => source.Head);
        object payload = scenario switch
        {
            "empty" => new { blockStateCalls = Array.Empty<object>() },
            "null-block" => new { blockStateCalls = new object?[] { null } },
            "null-call" => new { blockStateCalls = new[] { new { calls = new object?[] { null } } } },
            "too-many-blocks" => new { blockStateCalls = Enumerable.Repeat(new { calls = Array.Empty<object>() }, 17).ToArray() },
            "blocks-out-of-order" => new
            {
                blockStateCalls = new object[]
                {
                    new { blockOverrides = new { number = $"0x{source.Head.Number + 2:x}" } },
                    new { blockOverrides = new { number = $"0x{source.Head.Number + 1:x}" } },
                },
            },
            _ => new { blockStateCalls = "not an array" },
        };

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () =>
            await rpc.InvokeAsync("eth_simulateV1", JsonSerializer.SerializeToElement(new[] { payload }), CancellationToken.None));

        Assert.That(error!.Code, Is.EqualTo(expectedCode));
    }

    [Test]
    public async Task Call_uses_the_request_overrides_without_changing_the_authenticated_state([Values] bool replaceStorage)
    {
        byte[] readStorage = Bytes.FromHexString("0x60005460005260206000f3");
        FakeSource source = new(readStorage);
        SingleReleaseSpecProvider spec = new(Shanghai.Instance, 1, 1);
        using EvmExecutor calls = new(source, spec, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, calls, spec, () => source.Head);
        Dictionary<string, string> storage = new() { [Hash256.Zero.ToString()] = "0x" + 7.ToString("x64") };
        Dictionary<string, object> account = new()
        {
            [replaceStorage ? "state" : "stateDiff"] = storage,
            // Return the sum of TIME and storage slot zero.
            ["code"] = "0x426000540160005260206000f3",
        };
        var transaction = new { to = Contract.ToString() };
        object overridden = await rpc.InvokeAsync("eth_call", JsonSerializer.SerializeToElement(new object[]
        {
            transaction, "latest", new Dictionary<string, object> { [Contract.ToString()] = account }, new { time = "0x10" },
        }), CancellationToken.None);
        object normal = await rpc.InvokeAsync("eth_call", JsonSerializer.SerializeToElement(new[] { transaction }), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(overridden, Is.EqualTo("0x" + 23.ToString("x64")));
            Assert.That(normal, Is.EqualTo("0x" + 42.ToString("x64")));
            Assert.That(source.StorageFetches, Is.EqualTo(1));
            Assert.That(source.CodeFetches, Is.EqualTo(1));
            Assert.That(source.LastQueriedHead, Is.EqualTo(source.Head));
        }
    }

    [Test]
    public async Task EstimateGas_uses_overridden_code_and_reuses_the_overlay_for_every_probe()
    {
        FakeSource source = new([0x60, 0x00, 0x60, 0x00, 0xfd]);
        SingleReleaseSpecProvider spec = new(Shanghai.Instance, 1, 1);
        using EvmExecutor calls = new(source, spec, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, calls, spec, () => source.Head);
        var tx = new { to = Contract.ToString() };
        Dictionary<string, object> state = new() { [Contract.ToString()] = new { code = "0x00" } };

        object estimate = await rpc.InvokeAsync("eth_estimateGas", JsonSerializer.SerializeToElement(new object?[] { tx, null, state }),
            CancellationToken.None);
        RpcException? original = await Assert.ThrowsAsync<RpcException>(async () =>
            await rpc.InvokeAsync("eth_estimateGas", JsonSerializer.SerializeToElement(new[] { tx }), CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Convert.ToUInt64((string)estimate, 16), Is.InRange(21_000UL, 22_000UL));
            Assert.That(original!.Code, Is.EqualTo(3));
            Assert.That(source.CodeFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Block_only_override_supports_null_state_and_keeps_the_original_header()
    {
        FakeSource source = new(Bytes.FromHexString("0x4360005260206000f3"));
        SingleReleaseSpecProvider spec = new(Shanghai.Instance, 1, 1);
        using EvmExecutor calls = new(source, spec, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, calls, spec, () => source.Head);
        var tx = new { to = Contract.ToString() };
        object result = await rpc.InvokeAsync("eth_call", JsonSerializer.SerializeToElement(new object?[] { tx, "latest", null,
            new { number = "0x2a" } }), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo("0x" + 42.ToString("x64")));
            Assert.That(source.LastHeader!.Number, Is.EqualTo(source.Head.Number));
        }
    }

    [Test]
    public async Task CreateAccessList_reports_authenticated_storage_and_the_converged_gas([Values] bool optimize)
    {
        FakeSource source = new(Bytes.FromHexString("0x6001545000"));
        SingleReleaseSpecProvider spec = new(Shanghai.Instance, 1, 1);
        using EvmExecutor calls = new(source, spec, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, calls, spec, () => source.Head);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object?[] { new { to = Contract.ToString() }, "latest", null, optimize });

        JsonElement result = (JsonElement)await rpc.InvokeAsync("eth_createAccessList", parameters, CancellationToken.None);
        JsonElement entry = result.GetProperty("accessList").EnumerateArray().Single(item => item.GetProperty("address").GetString() == Contract.ToString());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.GetProperty("storageKeys")[0].GetString(), Is.EqualTo("0x" + 1.ToString("x64")));
            Assert.That(result.GetProperty("gasUsed").GetString(), Does.StartWith("0x"));
            Assert.That(result.TryGetProperty("error", out _), Is.False);
            Assert.That(source.StorageFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task CreateAccessList_rejects_pre_Berlin_calls()
    {
        FakeSource source = new([0x00]);
        SingleReleaseSpecProvider spec = new(Istanbul.Instance, 1, 1);
        using EvmExecutor calls = new(source, spec, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, calls, spec, () => source.Head);

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_createAccessList",
            JsonSerializer.SerializeToElement(new[] { new { to = Contract.ToString() } }), CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("before Berlin"));
    }

    [Test]
    public async Task TraceCall_reports_local_call_frames_and_validates_options([Values("ok", "txIndex", "timeout", "tracer", "budget")] string scenario)
    {
        byte[] code = scenario == "budget"
            ? Enumerable.Repeat<byte[]>([0x61, 0x80, 0x00, 0x60, 0x00, 0xa0], 17).SelectMany(static instruction => instruction).Append((byte)0).ToArray()
            : Bytes.FromHexString("0x60005460005260206000f3");
        FakeSource source = new(code);
        SingleReleaseSpecProvider spec = new(Shanghai.Instance, 1, 1);
        using EvmExecutor calls = new(source, spec, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, calls, spec, () => source.Head);
        object options = scenario switch
        {
            "txIndex" => new { tracer = "callTracer", txIndex = "0x0" },
            "timeout" => new { tracer = "callTracer", timeout = "bad-duration" },
            "tracer" => new { tracer = "prestateTracer" },
            "budget" => new { tracer = "callTracer", tracerConfig = new { withLog = true } },
            _ => new { tracer = "callTracer", timeout = "15s", tracerConfig = new { onlyTopCall = true } },
        };
        JsonElement parameters = JsonSerializer.SerializeToElement(new[] { new { to = Contract.ToString() }, "latest", options });
        if (scenario != "ok")
        {
            RpcException? failure = await Assert.ThrowsAsync<RpcException>(async () =>
                await rpc.InvokeAsync("debug_traceCall", parameters, CancellationToken.None));
            Assert.That(failure!.Code, Is.EqualTo(scenario == "budget" ? -32005 : -32602));
            return;
        }

        JsonElement trace = (JsonElement)await rpc.InvokeAsync("debug_traceCall", parameters, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.GetProperty("type").GetString(), Is.EqualTo("CALL"));
            Assert.That(trace.GetProperty("to").GetString(), Is.EqualTo(Contract.ToString()));
            Assert.That(trace.GetProperty("output").GetString(), Is.EqualTo("0x" + 42.ToString("x64")));
            Assert.That(source.StorageFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Unpriced_traceCall_simulates_zero_base_fee_even_with_a_base_fee_override()
    {
        FakeSource source = new(Bytes.FromHexString("0x4860005260206000f3"));
        SingleReleaseSpecProvider spec = new(Shanghai.Instance, 1, 1);
        using EvmExecutor calls = new(source, spec, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, calls, spec, () => source.Head);

        JsonElement trace = (JsonElement)await rpc.InvokeAsync("debug_traceCall", JsonSerializer.SerializeToElement(new object[]
        {
            new { to = Contract.ToString() }, "latest", new { tracer = "callTracer", blockOverrides = new { baseFeePerGas = "0xa" } },
        }), CancellationToken.None);

        Assert.That(trace.GetProperty("output").GetString(), Is.EqualTo(Hash256.Zero.ToString()));
    }

    [Test]
    public async Task TraceTransaction_replays_the_signed_prefix_against_authenticated_parent_state()
    {
        // An input word writes slot zero. An empty input returns the slot, then the parent and grandparent block hashes.
        FakeSource source = new(Bytes.FromHexString("0x3660215760005460005243600190034060205243600290034060405260606000f35b60003560005500"));
        using PrivateKey signer = new("0x" + 1.ToString("x64"));
        EthereumEcdsa ecdsa = new(1);
        Transaction Transaction(ulong nonce, ReadOnlyMemory<byte> data)
        {
            Transaction tx = new() { To = Contract, Nonce = nonce, GasLimit = 100_000, GasPrice = 2, Data = data };
            ecdsa.Sign(signer, tx);
            tx.Hash = tx.CalculateHash();
            return tx;
        }
        byte[] word = new byte[32];
        word[^1] = 7;
        Transaction write = Transaction(0, word);
        Transaction read = Transaction(1, ReadOnlyMemory<byte>.Empty);
        BlockHeader parent = new(Keccak.Compute("trace grandparent"), Keccak.OfAnEmptySequenceRlp, Address.Zero,
            UInt256.Zero, source.Head.Number - 1, 30_000_000, 1_790_000_000, [])
        {
            StateRoot = Keccak.Compute("trace parent state"),
            BaseFeePerGas = 1,
            IsPostMerge = true,
        };
        parent.Hash = parent.CalculateHash();
        BlockHeader header = new(parent.Hash, Keccak.OfAnEmptySequenceRlp, Address.Zero,
            UInt256.Zero, source.Head.Number, 30_000_000, 1_790_000_012, [])
        {
            Hash = source.Head.BlockHash,
            StateRoot = source.Head.StateRoot,
            BaseFeePerGas = 1,
            IsPostMerge = true,
            GasUsed = 55_000,
        };
        source.ReplayBlock = new(header, new BlockBody([write, read], []));
        source.ReplayParent = parent;
        source.ReplayReceipts = [new() { GasUsedTotal = 30_000 }, new() { GasUsedTotal = 55_000 }];
        source.ExtraAccounts[signer.Address] = new(0, 1_000_000);
        SingleReleaseSpecProvider spec = new(Shanghai.Instance, 1, 1);
        using EvmExecutor calls = new(source, spec, NullLogManager.Instance);
        using RpcBackend rpc = new(source, () => source.Head, 1, calls, spec, () => source.Head);

        JsonElement trace = (JsonElement)await rpc.InvokeAsync("debug_traceTransaction",
            JsonSerializer.SerializeToElement(new object[] { read.Hash!.ToString(), new { tracer = "callTracer", timeout = "30s" } }), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.GetProperty("output").GetString(), Is.EqualTo("0x" + 7.ToString("x64") + parent.Hash!.ToString()[2..] + parent.ParentHash!.ToString()[2..]));
            Assert.That(source.LastQueriedHead?.StateRoot, Is.EqualTo(parent.StateRoot));
            Assert.That(source.StorageFetches, Is.EqualTo(1));
            Assert.That(source.AncestorFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Transaction_trace_deadline_includes_the_transaction_lookup()
    {
        FakeSource source = new([0x00]);
        CallClock clock = new();
        using EvmExecutor calls = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance, clock);
        using CancellationTokenSource caller = new();
        TimeSpan timeout = TimeSpan.FromSeconds(3);
        Task<GethLikeTxTrace> request = calls.TraceTransactionAsync(new() { Tracer = "callTracer" }, timeout, async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }, caller.Token);
        try
        {
            clock.Expire();
            RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await request);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(error!.Code, Is.EqualTo(-32002));
                Assert.That(clock.DueTime, Is.EqualTo(timeout));
                Assert.That(source.Fetches, Is.Zero);
            }
        }
        finally
        {
            caller.Cancel();
            await ((Task)request).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static ILogManager DiagnosticLogs(bool debug, ConcurrentQueue<string> entries)
    {
        InterfaceLogger sink = Substitute.For<InterfaceLogger>();
        sink.IsDebug.Returns(debug);
        sink.When(logger => logger.Debug(Arg.Any<string>())).Do(call => entries.Enqueue(call.Arg<string>()));
        ILogManager logs = Substitute.For<ILogManager>();
        Nethermind.Logging.ILogger logger = new(sink);
        logs.GetLogger(EvmExecutor.LogCategory).Returns(logger);
        return logs;
    }

    private sealed class FakeSource(byte[] code, UInt256 contractBalance = default) : IExecutionStateSource
    {
        public Account ContractAccount { get; set; } = new(1, contractBalance, Keccak.Compute("storage root"), Keccak.Compute(code));
        public byte[] ContractCode { get; set; } = code;
        public VerifiedHead Head { get; set; } = new(100, 26_000_000, Keccak.Compute("block"), Keccak.Compute("state"));
        public int Fetches { get; private set; }
        public int CodeFetches { get; private set; }
        public int StorageFetches { get; private set; }
        public int AncestorFetches { get; private set; }
        public int ContractAccountFetches { get; private set; }
        public string? HeaderCorruption { get; init; }
        public Dictionary<Address, byte[]> ExtraContracts { get; } = [];
        public Dictionary<Address, Account> ExtraAccounts { get; } = [];
        public Block? ReplayBlock { get; set; }
        public BlockHeader? ReplayParent { get; set; }
        public TxReceipt[]? ReplayReceipts { get; set; }
        public ulong? FirstAncestorNumber { get; private set; }
        public UInt256 BaseFee { get; init; } = 1;
        public BlockHeader? LastHeader { get; private set; }
        public ulong BlockGasLimit { get; set; } = 30_000_000;
        public VerifiedHead? LastCanonicalAnchor { get; private set; }
        public VerifiedHead? LastQueriedHead { get; private set; }
        public Address? LastStorageAddress { get; private set; }
        public UInt256 LastStorageKey { get; private set; }
        public VerifiedHead? CanonicalHistoricalHead { get; set; }
        public Func<VerifiedHead, CancellationToken, Task<UInt256>>? StorageResponse { get; set; }
        public Func<VerifiedHead, Account>? ContractAccountResponse { get; set; }
        public int Broadcasts { get; private set; }
        public Task BroadcastTransactionAsync(Transaction transaction, CancellationToken cancellationToken)
        {
            Broadcasts++;
            return Task.CompletedTask;
        }

        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
        {
            Fetches++;
            if (ReplayBlock is { } replay && replay.Hash == head.BlockHash) return Task.FromResult(replay.Header);
            BlockHeader header = new(HashAt(head, head.Number - 1), Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero,
                head.Number + (HeaderCorruption == "number" ? 1UL : 0), BlockGasLimit, 1_790_000_000, [])
            {
                Hash = HeaderCorruption == "hash" ? Keccak.Compute("wrong block") : head.BlockHash,
                StateRoot = HeaderCorruption == "state" ? Keccak.Compute("wrong state") : head.StateRoot,
                BaseFeePerGas = BaseFee,
                MixHash = Keccak.Compute("random"),
                BlobGasUsed = 0,
                ExcessBlobGas = 0,
                IsPostMerge = true
            };
            LastHeader = header;
            return Task.FromResult(header);
        }

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
        {
            Fetches++;
            LastQueriedHead = head;
            if (ExtraAccounts.TryGetValue(address, out Account? account)) return Task.FromResult(account);
            if (ExtraContracts.TryGetValue(address, out byte[]? extra))
                return Task.FromResult(new Account(1, 0, Keccak.EmptyTreeHash, Keccak.Compute(extra)));
            if (contractBalance != UInt256.Zero && address == Eip8250Constants.NonceManagerAddress)
                return Task.FromResult(new Account(1, 0, Keccak.Compute("nonce root"), Keccak.OfAnEmptyString));
            if (address == Eip2935Constants.BlockHashHistoryAddress)
                return Task.FromResult(new Account(1, 0, Keccak.Compute("history root"), Keccak.OfAnEmptyString));
            if (address != Contract) return Task.FromResult(Account.TotallyEmpty);
            ContractAccountFetches++;
            return Task.FromResult(ContractAccountResponse?.Invoke(head) ?? ContractAccount);
        }

        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken)
        {
            Fetches++;
            StorageFetches++;
            LastStorageAddress = address;
            LastStorageKey = key;
            return StorageResponse?.Invoke(head, cancellationToken) ?? Task.FromResult((UInt256)42);
        }

        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken)
        {
            Fetches++;
            CodeFetches++;
            foreach (byte[] extra in ExtraContracts.Values)
                if (account.CodeHash == Keccak.Compute(extra)) return Task.FromResult(extra);
            Assert.That(account.CodeHash, Is.EqualTo(Keccak.Compute(ContractCode)), "Code must be fetched using its authenticated owner.");
            return Task.FromResult(ContractCode);
        }

        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken)
        {
            AncestorFetches++;
            FirstAncestorNumber = firstNumber;
            Assert.That(head.Number - firstNumber, Is.LessThanOrEqualTo(256));
            return Task.FromResult(Enumerable.Range(0, checked((int)(head.Number - firstNumber + 1)))
                .Select(index => ReplayParent is { } parent && parent.Hash == head.BlockHash && firstNumber + (ulong)index == parent.Number - 1
                    ? parent.ParentHash! : HashAt(head, firstNumber + (ulong)index)).ToArray());
        }

        internal static Hash256 HashAt(VerifiedHead head, ulong number) => number == head.Number ? head.BlockHash
            : head.BlockHash == Keccak.Compute("block") && number == head.Number - 1 ? Keccak.Compute("parent")
            : head.BlockHash == Keccak.Compute("block") && number == head.Number - 2 ? Keccak.Compute("grandparent")
            : Keccak.Compute($"ancestor:{head.BlockHash}:{number}");

        public Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken) =>
            hash == Head.BlockHash ? GetHeaderAsync(Head, cancellationToken)
                : ReplayParent is { } parent && parent.Hash == hash ? Task.FromResult(parent) : throw new NotSupportedException();

        public Task<Block> GetBlockAsync(BlockHeader header, CancellationToken cancellationToken) =>
            ReplayBlock is { } block && block.Hash == header.Hash ? Task.FromResult(block) : throw new NotSupportedException();

        public Task<TxReceipt[]> GetReceiptsAsync(BlockHeader header, CancellationToken cancellationToken, int? transactionCount = null) =>
            ReplayReceipts is { } receipts && ReplayBlock?.Hash == header.Hash ? Task.FromResult(receipts) : throw new NotSupportedException();

        public Task<BlockHeader> GetCanonicalHeaderAsync(VerifiedHead head, ulong number, CancellationToken cancellationToken)
        {
            LastCanonicalAnchor = head;
            if (head.Number != Head.Number + 1 || number != Head.Number) throw new AssertionException("Unexpected optimistic ancestor request.");
            return GetHeaderAsync(CanonicalHistoricalHead ?? Head, cancellationToken);
        }
    }
}
