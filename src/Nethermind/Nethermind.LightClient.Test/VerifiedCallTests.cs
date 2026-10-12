// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Serialization.Rlp;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class VerifiedCallTests
{
    private static readonly Address Contract = new("0x1234567890123456789012345678901234567890");

    [Test]
    public async Task Frame_submission_verifies_payer_prefix_and_keyed_nonce_before_relay(
        [Values("valid", "dimension-fits", "blob-valid", "nonce", "prefix", "signature", "execution-fee", "blob-fee", "execution-reservation", "state-reservation")] string scenario)
    {
        byte[] code = scenario == "prefix" ? [0x00] : [0x60, 0x03, 0x60, 0x00, 0x60, 0x00, 0xaa, 0x00];
        FakeSource source = new(code, contractBalance: 1_000_000_000) { BaseFee = (UInt256)(scenario == "execution-fee" ? 10 : 1) };
        SingleReleaseSpecProvider spec = new(new KeyedFrameSpec(), 1, 1);
        using VerifiedCall calls = new(source, spec, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, calls, spec, () => source.Head);
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
                200_000, scenario == "state-reservation" ? 300_000UL : 200_000UL, UInt256.Zero, default)],
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
        if (scenario is "valid" or "dimension-fits" or "blob-valid")
        {
            Assert.That(await rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None), Is.EqualTo(transaction.CalculateHash().ToString()));
            Assert.That(source.Broadcasts, Is.EqualTo(1));
            Assert.That(source.StorageFetches, Is.GreaterThan(0));
            Assert.That(source.CodeFetches, Is.EqualTo(1));
        }
        else
        {
            RpcException? error = Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_sendRawTransaction", parameters, CancellationToken.None));
            Assert.That(error!.Code, Is.EqualTo(-32003));
            Assert.That(source.Broadcasts, Is.Zero);
            if (scenario is "execution-fee" or "blob-fee" or "execution-reservation" or "state-reservation")
            {
                Assert.That(error.Message, Does.Contain(scenario switch
                {
                    "execution-fee" => "fee cap is below",
                    "blob-fee" => "blob fee cap is below",
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
    public async Task Blob_call_and_estimate_run_in_the_local_evm([Values("eth_call", "eth_estimateGas")] string method)
    {
        // BLOBHASH(0), MSTORE(0), RETURN(0,32).
        FakeSource source = new([0x60, 0x00, 0x49, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3], contractBalance: 1_000_000_000);
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);
        string versionedHash = "0x01" + new string('0', 62);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { new
        {
            type = "0x3", from = Contract.ToString(), to = Contract.ToString(), gas = "0x186a0",
            maxFeePerGas = "0x2", maxPriorityFeePerGas = "0x1", maxFeePerBlobGas = "0x2",
            blobVersionedHashes = new[] { versionedHash },
        }, "finalized" });
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
    public void Revert_is_an_error_with_return_data([Values("eth_call", "eth_estimateGas")] string method)
    {
        FakeSource source = new([0x60, 0x00, 0x60, 0x00, 0xfd]);
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);

        RpcException? error = Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync(method,
            JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" }), CancellationToken.None));

        Assert.That(error!.Code, Is.EqualTo(3));
        Assert.That(error.RevertData, Is.EqualTo("0x"));
    }

    [Test]
    public async Task Blockhash_uses_a_chain_anchored_to_finality()
    {
        // PUSH4(26,000,000 - 2), BLOCKHASH, MSTORE, RETURN.
        FakeSource source = new([0x63, 0x01, 0x8c, 0xba, 0x7e, 0x40, 0x60, 0x00, 0x52,
            0x60, 0x20, 0x60, 0x00, 0xf3]);
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);

        object result = await rpc.InvokeAsync("eth_call", JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" }), CancellationToken.None);

        Assert.That(result, Is.EqualTo(Keccak.Compute("grandparent").ToString()));
        Assert.That(source.AncestorFetches, Is.EqualTo(1));
    }

    [Test]
    public void Header_must_match_the_finalized_state_root()
    {
        FakeSource source = new([0x00]) { WrongHeaderRoot = true };
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);

        RpcException? error = Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync("eth_call",
            JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" }), CancellationToken.None));

        Assert.That(error!.Code, Is.EqualTo(-32000));
        Assert.That(source.Fetches, Is.EqualTo(1));
    }

    [Test]
    public void Execution_rejects_unverified_selector_before_fetch([Values("eth_call", "eth_estimateGas")] string method,
        [Values("latest", "pending")] string selector)
    {
        FakeSource source = new([0x00]);
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        using VerifiedRpc rpc = new(source, () => source.Head, 1, call);

        RpcException? error = Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync(method,
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
        RpcException? orphan = Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync(method,
            JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, new { blockHash = source.Head.BlockHash.ToString(), requireCanonical = true } }), CancellationToken.None));
        Assert.That(orphan!.Code, Is.EqualTo(-32001));
    }

    private sealed class FakeSource(byte[] code, UInt256 contractBalance = default) : IExecutionStateSource
    {
        private readonly Account _contract = new(1, contractBalance, Keccak.Compute("storage root"), Keccak.Compute(code));
        public VerifiedHead Head { get; } = new(100, 26_000_000, Keccak.Compute("block"), Keccak.Compute("state"));
        public int Fetches { get; private set; }
        public int CodeFetches { get; private set; }
        public int StorageFetches { get; private set; }
        public int AncestorFetches { get; private set; }
        public bool WrongHeaderRoot { get; init; }
        public UInt256 BaseFee { get; init; } = 1;
        public ulong BlockGasLimit { get; set; } = 30_000_000;
        public VerifiedHead? LastCanonicalAnchor { get; private set; }
        public VerifiedHead? LastQueriedHead { get; private set; }
        public VerifiedHead? CanonicalHistoricalHead { get; set; }
        public int Broadcasts { get; private set; }
        public Task BroadcastTransactionAsync(Transaction transaction, CancellationToken cancellationToken)
        {
            Broadcasts++;
            return Task.CompletedTask;
        }

        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
        {
            Fetches++;
            BlockHeader header = new(Keccak.Compute("parent"), Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero,
                head.Number, BlockGasLimit, 1_790_000_000, [])
            {
                Hash = head.BlockHash,
                StateRoot = WrongHeaderRoot ? Keccak.Compute("wrong state") : head.StateRoot,
                BaseFeePerGas = BaseFee,
                MixHash = Keccak.Compute("random"),
                BlobGasUsed = 0,
                ExcessBlobGas = 0,
                IsPostMerge = true
            };
            return Task.FromResult(header);
        }

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
        {
            Fetches++;
            LastQueriedHead = head;
            if (contractBalance != UInt256.Zero && address == Eip8250Constants.NonceManagerAddress)
                return Task.FromResult(new Account(1, 0, Keccak.Compute("nonce root"), Keccak.OfAnEmptyString));
            return Task.FromResult(address == Contract ? _contract : Account.TotallyEmpty);
        }

        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken)
        {
            Fetches++;
            StorageFetches++;
            return Task.FromResult((UInt256)42);
        }

        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken)
        {
            Fetches++;
            CodeFetches++;
            return Task.FromResult(code);
        }

        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken)
        {
            AncestorFetches++;
            if (firstNumber != head.Number - 2) throw new AssertionException("Unexpected ancestor request.");
            return Task.FromResult(new[] { Keccak.Compute("grandparent"), Keccak.Compute("parent"), head.BlockHash });
        }

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
