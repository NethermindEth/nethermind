// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class VerifiedCallTests
{
    private static readonly Address Contract = new("0x1234567890123456789012345678901234567890");

    [Test]
    public async Task Call_executes_verified_bytecode_and_reuses_fetched_state()
    {
        // Return the 32-byte word 42.
        FakeSource source = new([0x60, 0x2a, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3]);
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        VerifiedRpc rpc = new(source, () => source.Head, 1, call);
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
        VerifiedRpc rpc = new(source, () => source.Head, 1, call);

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
        VerifiedRpc rpc = new(source, () => source.Head, 1, call);
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
        VerifiedRpc rpc = new(source, () => source.Head, 1, call);
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
        VerifiedRpc rpc = new(source, () => source.Head, 1, call);

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
        VerifiedRpc rpc = new(source, () => source.Head, 1, call);

        object result = await rpc.InvokeAsync("eth_call", JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, "finalized" }), CancellationToken.None);

        Assert.That(result, Is.EqualTo(Keccak.Compute("grandparent").ToString()));
        Assert.That(source.AncestorFetches, Is.EqualTo(1));
    }

    [Test]
    public void Header_must_match_the_finalized_state_root()
    {
        FakeSource source = new([0x00]) { WrongHeaderRoot = true };
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        VerifiedRpc rpc = new(source, () => source.Head, 1, call);

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
        VerifiedRpc rpc = new(source, () => source.Head, 1, call);

        RpcException? error = Assert.ThrowsAsync<RpcException>(async () => await rpc.InvokeAsync(method,
            JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() }, selector }), CancellationToken.None));

        Assert.That(error!.Code, Is.EqualTo(selector == "latest" ? -32001 : -32602));
        Assert.That(source.Fetches, Is.Zero);
    }

    [Test]
    public async Task Execution_defaults_missing_selector_to_latest([Values("eth_call", "eth_estimateGas")] string method)
    {
        FakeSource source = new([0x00]);
        using VerifiedCall call = new(source, MainnetSpecProvider.Instance, NullLogManager.Instance);
        VerifiedRpc rpc = new(source, () => throw new AssertionException("The finalized head must not be selected."),
            1, call, getLatestHead: () => source.Head);

        object result = await rpc.InvokeAsync(method,
            JsonSerializer.SerializeToElement(new object[] { new { to = Contract.ToString() } }), CancellationToken.None);

        if (method == "eth_call") Assert.That(result, Is.EqualTo("0x"));
        else Assert.That(Convert.ToUInt64(((string)result)[2..], 16), Is.InRange(21_000, 30_000));
        Assert.That(source.Fetches, Is.GreaterThan(0));
    }

    private sealed class FakeSource(byte[] code) : IExecutionStateSource
    {
        private readonly Account _contract = new(1, 0, Keccak.Compute("storage root"), Keccak.Compute(code));
        public VerifiedHead Head { get; } = new(100, 26_000_000, Keccak.Compute("block"), Keccak.Compute("state"));
        public int Fetches { get; private set; }
        public int CodeFetches { get; private set; }
        public int StorageFetches { get; private set; }
        public int AncestorFetches { get; private set; }
        public bool WrongHeaderRoot { get; init; }

        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
        {
            Fetches++;
            BlockHeader header = new(Keccak.Compute("parent"), Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero,
                head.Number, 30_000_000, 1_790_000_000, [])
            {
                Hash = head.BlockHash,
                StateRoot = WrongHeaderRoot ? Keccak.Compute("wrong state") : head.StateRoot,
                BaseFeePerGas = 1,
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
    }
}
