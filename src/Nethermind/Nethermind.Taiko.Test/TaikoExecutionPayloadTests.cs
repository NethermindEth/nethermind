// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Proofs;
using Nethermind.Taiko.TaikoSpec;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

/// <summary>
/// Tests for the temporal coupling between
/// <see cref="TaikoExecutionPayload.AttachSpecProvider"/> and
/// <see cref="TaikoExecutionPayload.TryGetBlock"/>: the spec provider must be attached
/// before the block is reconstructed so the Unzen-pinned header fields
/// (<c>ParentBeaconBlockRoot</c>, <c>RequestsHash</c>) are restored deterministically.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class TaikoExecutionPayloadTests
{
    /// <summary>Builds a minimal payload that exercises the empty-block branch of <see cref="TaikoExecutionPayload.TryGetBlock"/>.</summary>
    private static TaikoExecutionPayload BuildEmptyPayload() => new()
    {
        ParentHash = Keccak.Zero,
        FeeRecipient = Address.Zero,
        StateRoot = Keccak.Zero,
        ReceiptsRoot = Keccak.Zero,
        LogsBloom = Bloom.Empty,
        PrevRandao = Keccak.Zero,
        BlockNumber = 1,
        GasLimit = 30_000_000,
        GasUsed = 0,
        Timestamp = 1,
        ExtraData = [],
        BaseFeePerGas = 1,
        BlockHash = Keccak.Zero,
        // null Withdrawals + null Transactions selects the empty-block path
    };

    [Test]
    public void TryGetBlock_throws_when_AttachSpecProvider_was_not_called()
    {
        TaikoExecutionPayload payload = BuildEmptyPayload();

        Action act = () => payload.TryGetBlock();

        Assert.That(act, Throws.TypeOf<InvalidOperationException>()
            .With.Message.Contains(nameof(TaikoExecutionPayload.AttachSpecProvider))
            .And.Message.Contains(nameof(TaikoExecutionPayload.TryGetBlock)));
    }

    [Test]
    public void TryGetBlock_pins_Unzen_fields_when_spec_active()
    {
        TaikoExecutionPayload payload = BuildEmptyPayload();
        payload.AttachSpecProvider(new TestSpecProvider(new TaikoReleaseSpec { IsUnzenEnabled = true, TaikoL2Address = Address.Zero }));

        Result<Block> result = payload.TryGetBlock();

        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Header.ParentBeaconBlockRoot, Is.EqualTo(Keccak.Zero));
        Assert.That(result.Data.Header.RequestsHash, Is.EqualTo(Nethermind.Core.ExecutionRequest.ExecutionRequestExtensions.EmptyRequestsHash));
        // A strict V2 driver (Rust) omits the blob gas fields; Unzen must still pin them to 0
        // so the reconstructed hash matches the producer's.
        Assert.That(result.Data.Header.BlobGasUsed, Is.EqualTo(0UL));
        Assert.That(result.Data.Header.ExcessBlobGas, Is.EqualTo(0UL));
    }

    [Test]
    public void TryGetBlock_normalizes_nonzero_blob_gas_to_zero_when_Unzen_active()
    {
        TaikoExecutionPayload payload = BuildEmptyPayload();
        payload.BlobGasUsed = 5;
        payload.ExcessBlobGas = 7;
        payload.AttachSpecProvider(new TestSpecProvider(new TaikoReleaseSpec { IsUnzenEnabled = true, TaikoL2Address = Address.Zero }));

        Result<Block> result = payload.TryGetBlock();

        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Header.BlobGasUsed, Is.EqualTo(0UL));
        Assert.That(result.Data.Header.ExcessBlobGas, Is.EqualTo(0UL));
    }

    // The shadowing setter must reach the base setter's memo invalidation: a tx-root task
    // memoized by an earlier TryGetBlock must never supply the root for reassigned transactions
    [Test]
    public void TryGetBlock_recomputes_tx_root_when_transactions_change_between_calls()
    {
        TaikoExecutionPayload payload = BuildEmptyPayload();
        payload.AttachSpecProvider(new TestSpecProvider(new TaikoReleaseSpec { IsUnzenEnabled = false, TaikoL2Address = Address.Zero }));

        payload.Transactions = EncodeTxs(count: 64);
        Hash256? originalRoot = payload.TryGetBlock().Data!.Header.TxRoot;

        byte[][] replacementRlps = EncodeTxs(count: 64, nonceOffset: 1000);
        payload.Transactions = replacementRlps;
        Hash256? replacementRoot = payload.TryGetBlock().Data!.Header.TxRoot;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replacementRoot, Is.EqualTo(TxTrie.CalculateRoot(replacementRlps)));
            Assert.That(replacementRoot, Is.Not.EqualTo(originalRoot));
        }
    }

    // The delegating setter must preserve the Taiko null <-> empty round-trip
    [Test]
    public void Transactions_setter_preserves_null_round_trip()
    {
        TaikoExecutionPayload payload = new() { Transactions = EncodeTxs(count: 1) };

        payload.Transactions = null;

        Assert.That(payload.Transactions, Is.Null);
    }

    private static byte[][] EncodeTxs(int count, ulong nonceOffset = 0)
    {
        byte[][] rlps = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            Transaction tx = Build.A.Transaction.WithNonce(nonceOffset + (ulong)i).SignedAndResolved().TestObject;
            rlps[i] = TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;
        }
        return rlps;
    }

    [Test]
    public void TryGetBlock_does_not_pin_Unzen_fields_when_spec_inactive()
    {
        TaikoExecutionPayload payload = BuildEmptyPayload();
        payload.BlobGasUsed = 5;
        payload.ExcessBlobGas = 7;
        payload.AttachSpecProvider(new TestSpecProvider(new TaikoReleaseSpec { IsUnzenEnabled = false, TaikoL2Address = Address.Zero }));

        Result<Block> result = payload.TryGetBlock();

        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Header.ParentBeaconBlockRoot, Is.Null);
        Assert.That(result.Data.Header.RequestsHash, Is.Null);
        Assert.That(result.Data.Header.BlobGasUsed, Is.Null);
        Assert.That(result.Data.Header.ExcessBlobGas, Is.Null);
    }
}
