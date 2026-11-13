// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.LightClient.Rpc;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Serialization.Rlp;
using NSubstitute;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class VerifiedCallTests
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
        using VerifiedCall calls = new(source, spec, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, calls, spec, () => source.Head);
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
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Contract,
            NonceKeys = [(UInt256)7],
            Nonce = scenario == "nonce" ? 41UL : 42UL,
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
            MaxFeePerBlobGas = UInt256.Zero,
            BlobVersionedHashes = [],
            FrameSignatures = [],
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, null,
                executionGas, stateGas, UInt256.Zero, default)],
        };
        if (scenario == "signature")
            transaction.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, Contract, default,
                new byte[TxFrameSignature.Secp256k1SignatureLength])];
        if (scenario is "blob-valid" or "blob-fee")
        {
            await KzgPolynomialCommitments.InitializeAsync(NullLogManager.Instance.GetClassLogger<VerifiedCallTests>());
            IBlobProofsManager proofs = IBlobProofsManager.For(ProofVersion.V1);
            ShardBlobNetworkWrapper wrapper = proofs.AllocateWrapper(new byte[131_072]);
            proofs.ComputeProofsAndCommitments(wrapper);
            transaction.BlobVersionedHashes = proofs.ComputeHashes(wrapper);
            transaction.NetworkWrapper = wrapper;
            transaction.MaxFeePerBlobGas = (UInt256)(scenario == "blob-fee" ? 0 : 2);
        }
        transaction.GasLimit = FrameTxValidation.TotalGasLimit(transaction.Frames);
        byte[] encoded = TxDecoder.Instance.EncodeAsBytes(transaction, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
        RlpReader reader = new(encoded.AsMemory());
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
            Assert.That(await rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None), Is.EqualTo(transaction.CalculateHash().ToString()));
            Assert.That(source.Broadcasts, Is.EqualTo(1));
            Assert.That(source.StorageFetches, Is.GreaterThan(0));
            Assert.That(source.CodeFetches, Is.EqualTo(1));
            int storageFetches = source.StorageFetches;
            source.Head = new(source.Head.Slot + 1, source.Head.Number + 1, Keccak.Compute("next frame block"), Keccak.Compute("next frame state"));

            Assert.That(await rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None), Is.EqualTo(transaction.CalculateHash().ToString()));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(source.Broadcasts, Is.EqualTo(2));
                Assert.That(source.CodeFetches, Is.EqualTo(1));
                Assert.That(source.StorageFetches, Is.EqualTo(storageFetches));
                Assert.That(source.ContractAccountFetches, Is.EqualTo(2));
            }
        }
        else
        {
            RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None));
            Assert.That(error!.Code, Is.EqualTo(-32003));
            Assert.That(source.Broadcasts, Is.Zero);
            if (scenario is "prefix-opcode" or "prefix-opcode-abort")
                Assert.That(error.Message, Does.Contain("banned opcode NUMBER"));
            if (scenario is "execution-fee" or "blob-fee" or "execution-reservation" or "state-reservation" or "verify-gas-over" or "verify-state-over")
            {
                Assert.That(error.Message, Does.Contain(scenario switch
                {
                    "execution-fee" => "fee cap is below",
                    "blob-fee" => "blob fee cap is below",
                    "verify-gas-over" or "verify-state-over" => "bounded public-mempool work limit",
                    _ => "gas reservation exceeds",
                }));
                Assert.That(source.CodeFetches, Is.Zero);
                Assert.That(source.StorageFetches, Is.Zero);
            }
        }
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);
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
        using VerifiedCall call = new(source, spec, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" });
        await rpc.InvokeAsync(method, parameters, CancellationToken.None);
        source.Head = new(source.Head.Slot + 1, source.Head.Number + 1, Keccak.Compute("unproven block"), Keccak.Compute("unproven state"));
        source.ContractAccountResponse = _ => throw new InvalidDataException("Account proof failed.");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await rpc.InvokeAsync(method, parameters, CancellationToken.None));

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
        using VerifiedCall calls = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);
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
    public async Task Call_fetches_storage_touched_by_evm()
    {
        // SLOAD(0), MSTORE(0), RETURN(0, 32).
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);

        object result = await rpc.InvokeAsync("eth_call", JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" }), CancellationToken.None);

        Assert.That(result, Is.EqualTo("0x" + new string('0', 62) + "2a"));
        Assert.That(source.StorageFetches, Is.EqualTo(1));
    }

    [Test]
    public async Task Call_writes_are_discarded_between_requests()
    {
        // Return SLOAD(0) + 1 after writing it; the next call must still read the finalized 42.
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x01, 0x01, 0x80, 0x60, 0x00, 0x55,
            0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" });

        object first = await rpc.InvokeAsync("eth_call", parameters, CancellationToken.None);
        object second = await rpc.InvokeAsync("eth_call", parameters, CancellationToken.None);

        Assert.That(first, Is.EqualTo("0x" + new string('0', 62) + "2b"));
        Assert.That(second, Is.EqualTo(first));
        Assert.That(source.StorageFetches, Is.EqualTo(1));
    }

    [Test]
    public async Task Estimate_uses_verified_state_and_reuses_fetched_proofs()
    {
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);

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
        using VerifiedCall call = new(source, specs, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);

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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);

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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);

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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
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
        using VerifiedMemoryCache cache = new();
        for (int i = 0; i < 32; i++) cache.Set(i, i, VerifiedMemoryCache.Capacity / 32);
        Assert.That(cache.TryGetValue(0, out int first), Is.True);
        Assert.That(first, Is.Zero);
        cache.Set("large", 42, VerifiedMemoryCache.Capacity / 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGetValue(0, out int retained), Is.True);
            Assert.That(retained, Is.Zero);
            Assert.That(cache.TryGetValue(1, out int _), Is.False);
            Assert.That(cache.TryGetValue("large", out int large), Is.True);
            Assert.That(large, Is.EqualTo(42));
        }
        cache.Set("large", 43, VerifiedMemoryCache.Capacity);
        cache.Set("oversized", 99, VerifiedMemoryCache.Capacity + 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGetValue(0, out int _), Is.False);
            Assert.That(cache.TryGetValue("large", out int replaced), Is.True);
            Assert.That(replaced, Is.EqualTo(43));
            Assert.That(cache.TryGetValue("oversized", out int _), Is.False);
        }
    }

    [Test]
    public async Task Network_fills_do_not_hold_execution_permits_or_starve_a_cached_call()
    {
        FakeSource source = new([0x60, 0x00, 0x54, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        Address warm = new("0x3234567890123456789012345678901234567890");
        source.ExtraContracts.Add(warm, [0x60, 0x2a, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
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
            Assert.That(await cached.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(expected));
            Assert.That(slow, Has.All.Property(nameof(Task.IsCompleted)).EqualTo(false));
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance, clock);
        SemaphoreSlim slots = (SemaphoreSlim)typeof(VerifiedCall).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(call)!;
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_call",
            JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" }), CancellationToken.None));

        Assert.That(error!.Code, Is.EqualTo(-32000));
        Assert.That(source.Fetches, Is.EqualTo(1));
    }

    [Test]
    public async Task Execution_rejects_unverified_selector_before_fetch([Values("eth_call", "eth_estimateGas")] string method,
        [Values("latest", "pending")] string selector)
    {
        FakeSource source = new([0x00]);
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);

        RpcException? error = await Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync(method,
            JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, selector }), CancellationToken.None));

        Assert.That(error!.Code, Is.EqualTo(-32001));
        Assert.That(source.Fetches, Is.Zero);
    }

    [Test]
    public async Task Execution_defaults_missing_selector_to_latest([Values("eth_call", "eth_estimateGas")] string method)
    {
        FakeSource source = new([0x00]);
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => throw new AssertionException("The finalized head must not be selected."),
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => finalized, 1, call, getLatestHead: () => latest);

        object result = await rpc.InvokeAsync(method, JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, selector }), CancellationToken.None);

        if (method == "eth_call") Assert.That(result, Is.EqualTo("0x" + new string('0', 62) + "2a"));
        else Assert.That(Convert.ToUInt64(((string)result)[2..], 16), Is.InRange(21_000, 30_000));
        Assert.That(source.LastCanonicalAnchor, Is.EqualTo(latest));
        Assert.That(source.LastQueriedHead!.BlockHash, Is.EqualTo(source.Head.BlockHash));
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, logs);
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, DiagnosticLogs(true, entries));
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, DiagnosticLogs(true, entries));
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
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, DiagnosticLogs(false, entries));
        using Activity parent = new Activity("test-wallet-request").SetIdFormat(ActivityIdFormat.W3C).Start();
        JsonElement transaction = JsonSerializer.SerializeToElement(new { to = Contract.ToString() });
        Task<string> request = call.ExecuteAsync(source.Head, transaction, false, CancellationToken.None);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            FieldInfo logger = typeof(VerifiedCall).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!;
            logger.SetValue(call, DiagnosticLogs(true, entries).GetClassLogger<VerifiedCall>());
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

    private static ILogManager DiagnosticLogs(bool debug, ConcurrentQueue<string> entries)
    {
        InterfaceLogger sink = Substitute.For<InterfaceLogger>();
        sink.IsDebug.Returns(debug);
        sink.When(logger => logger.Debug(Arg.Any<string>())).Do(call => entries.Enqueue(call.Arg<string>()));
        ILogManager logs = Substitute.For<ILogManager>();
        Nethermind.Logging.ILogger logger = new(sink);
        logs.GetClassLogger<VerifiedCall>().Returns(logger);
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
                .Select(index => HashAt(head, firstNumber + (ulong)index)).ToArray());
        }

        internal static Hash256 HashAt(VerifiedHead head, ulong number) => number == head.Number ? head.BlockHash
            : head.BlockHash == Keccak.Compute("block") && number == head.Number - 1 ? Keccak.Compute("parent")
            : head.BlockHash == Keccak.Compute("block") && number == head.Number - 2 ? Keccak.Compute("grandparent")
            : Keccak.Compute($"ancestor:{head.BlockHash}:{number}");

        public Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken) =>
            hash == Head.BlockHash ? GetHeaderAsync(Head, cancellationToken) : throw new NotSupportedException();

        public Task<BlockHeader> GetCanonicalHeaderAsync(VerifiedHead head, ulong number, CancellationToken cancellationToken)
        {
            LastCanonicalAnchor = head;
            if (head.Number != Head.Number + 1 || number != Head.Number) throw new AssertionException("Unexpected optimistic ancestor request.");
            return GetHeaderAsync(CanonicalHistoricalHead ?? Head, cancellationToken);
        }
    }
}
