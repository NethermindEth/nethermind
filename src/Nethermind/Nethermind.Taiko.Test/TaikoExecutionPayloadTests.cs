// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs;
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

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AttachSpecProvider*TryGetBlock*");
    }

    [Test]
    public void TryGetBlock_pins_Unzen_fields_when_spec_active()
    {
        TaikoExecutionPayload payload = BuildEmptyPayload();
        payload.AttachSpecProvider(new TestSpecProvider(new TaikoReleaseSpec { IsUnzenEnabled = true, TaikoL2Address = Address.Zero }));

        BlockDecodingResult result = payload.TryGetBlock();

        result.Block.Should().NotBeNull();
        result.Block!.Header.ParentBeaconBlockRoot.Should().Be(Keccak.Zero);
        result.Block.Header.RequestsHash.Should().Be(ExecutionRequestExtensions.EmptyRequestsHash);
    }

    [Test]
    public void TryGetBlock_does_not_pin_Unzen_fields_when_spec_inactive()
    {
        TaikoExecutionPayload payload = BuildEmptyPayload();
        payload.AttachSpecProvider(new TestSpecProvider(new TaikoReleaseSpec { IsUnzenEnabled = false, TaikoL2Address = Address.Zero }));

        BlockDecodingResult result = payload.TryGetBlock();

        result.Block.Should().NotBeNull();
        result.Block!.Header.ParentBeaconBlockRoot.Should().BeNull();
        result.Block.Header.RequestsHash.Should().BeNull();
    }
}
