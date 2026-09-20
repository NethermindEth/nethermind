// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// Framing and DoS-limit tests for the data column sidecar req/resp protocols, mirroring
/// <c>ReqRespLimitsTests</c>' pattern for the block protocols.
/// </summary>
public class DataColumnSidecarsReqRespTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    [Test]
    public async Task Response_exceeding_the_chunk_limit_is_rejected_and_the_stream_closed()
    {
        const int maxSidecars = 3;
        TestDataColumnSidecarsProtocol protocol = new(Spec);

        using MemoryStream stream = new();
        for (int i = 0; i < maxSidecars + 2; i++)
        {
            await WriteSidecarChunkAsync(stream, DataColumnSidecarTestFixture.BuildValidSidecar(5, slot: 1_000 + (ulong)i, seed: (byte)(0x40 + i)));
        }

        stream.Position = 0;

        long before = FailureCount(TestDataColumnSidecarsProtocol.ProtocolId, ReqRespFailureReason.LimitExceeded);

        Eth2ReqRespException? thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() => protocol.ReadSidecarsAsync(stream, maxSidecars));
        Assert.That(thrown!.Message, Does.Contain(maxSidecars.ToString()));
        Assert.That(FailureCount(TestDataColumnSidecarsProtocol.ProtocolId, ReqRespFailureReason.LimitExceeded), Is.EqualTo(before + 1), "limit violation recorded");
        Assert.That(stream.Position, Is.LessThan(stream.Length), "stream was closed to the peer before it was fully drained");
    }

    [Test]
    public async Task Response_at_exactly_the_chunk_limit_is_accepted()
    {
        const int maxSidecars = 3;
        TestDataColumnSidecarsProtocol protocol = new(Spec);

        using MemoryStream stream = new();
        for (int i = 0; i < maxSidecars; i++)
        {
            await WriteSidecarChunkAsync(stream, DataColumnSidecarTestFixture.BuildValidSidecar(5, slot: 2_000 + (ulong)i, seed: (byte)(0x50 + i)));
        }

        stream.Position = 0;

        IReadOnlyList<DataColumnSidecar> sidecars = await protocol.ReadSidecarsAsync(stream, maxSidecars);
        Assert.That(sidecars, Has.Count.EqualTo(maxSidecars));
    }

    [Test]
    public async Task A_chunk_with_mismatched_fork_digest_context_bytes_is_rejected()
    {
        TestDataColumnSidecarsProtocol protocol = new(Spec);
        DataColumnSidecar sidecar = DataColumnSidecarTestFixture.BuildValidSidecar(5, slot: 3_000);

        using MemoryStream stream = new();
        byte[] wrongContext = [0xDE, 0xAD, 0xBE, 0xEF];
        await ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, wrongContext, DataColumnSidecar.Encode(sidecar), default);
        stream.Position = 0;

        Eth2ReqRespException ex = Assert.ThrowsAsync<Eth2ReqRespException>(() => protocol.ReadSidecarsAsync(stream, 10))!;
        Assert.That(ex.Message, Does.Contain("context bytes"));
    }

    [TestCase(0)]
    [TestCase(Eip7594DasConstants.NumberOfColumns + 1)]
    public void DialAsync_rejects_an_invalid_columns_count_before_writing_to_the_wire(int columnCount)
    {
        DataColumnSidecarsByRangeProtocol protocol = new(Spec, new DataColumnSidecarPool());
        DataColumnSidecarsByRangeRequest request = new() { StartSlot = 0, Count = 1, Columns = new ulong[columnCount] };

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => protocol.DialAsync(null!, null!, request));
    }

    [Test]
    public void DialAsync_rejects_more_identifiers_than_MaxRequestBlocks_before_writing_to_the_wire()
    {
        DataColumnSidecarsByRootProtocol protocol = new(Spec, new DataColumnSidecarPool());
        DataColumnsByRootIdentifier[] request = new DataColumnsByRootIdentifier[BlocksProtocolBase.MaxRequestBlocks + 1];
        for (int i = 0; i < request.Length; i++)
        {
            request[i] = new DataColumnsByRootIdentifier { BlockRoot = Hash256.Zero, Columns = [0] };
        }

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => protocol.DialAsync(null!, null!, request));
    }

    [Test]
    public async Task Exactly_MaxRequestBlocks_identifiers_at_full_columns_hits_MaxRequestDataColumnSidecars_exactly_and_is_not_rejected_for_being_oversized()
    {
        // MaxRequestBlocks (128) identifiers x NumberOfColumns (128) columns each = exactly
        // MaxRequestDataColumnSidecars (16384): the per-identifier and per-request structural bounds
        // make this the largest shape reachable at all, so the request-level total check can reject
        // only a request that already violates one of those two bounds, never this one on its own.
        ulong[] allColumns = new ulong[Eip7594DasConstants.NumberOfColumns];
        for (int i = 0; i < allColumns.Length; i++)
        {
            allColumns[i] = (ulong)i;
        }

        DataColumnsByRootIdentifier[] request = new DataColumnsByRootIdentifier[BlocksProtocolBase.MaxRequestBlocks];
        for (int i = 0; i < request.Length; i++)
        {
            request[i] = new DataColumnsByRootIdentifier { BlockRoot = Hash256.Zero, Columns = allColumns };
        }

        Assert.That((ulong)request.Length * (ulong)allColumns.Length, Is.EqualTo(DataColumnSidecarsProtocolBase.MaxRequestDataColumnSidecars));

        // Encoding this large a request must not throw on its own (the size validation happens first
        // and accepts it); it fails later only because there is no real channel behind this test.
        Assert.ThrowsAsync<NullReferenceException>(() =>
            new DataColumnSidecarsByRootProtocol(Spec, new DataColumnSidecarPool()).DialAsync(null!, null!, request));
        await Task.CompletedTask;
    }

    private static long FailureCount(string protocolId, ReqRespFailureReason reason) =>
        Metrics.BeaconChainReqRespFailures.TryGetValue(new ReqRespFailureKey(protocolId, reason), out long count) ? count : 0;

    private static Task WriteSidecarChunkAsync(Stream stream, DataColumnSidecar sidecar)
    {
        byte[] contextBytes = ForkDigest.Compute(Spec, Spec.GetEpoch(sidecar.SignedBlockHeader!.Message!.Slot));
        return ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, contextBytes, DataColumnSidecar.Encode(sidecar), default);
    }

    /// <summary>Exposes the protected chunked-response reader for direct testing.</summary>
    private sealed class TestDataColumnSidecarsProtocol(BeaconChainSpec spec) : DataColumnSidecarsProtocolBase(spec)
    {
        public const string ProtocolId = "/test/data-column-sidecars-limits/1";

        public Task<IReadOnlyList<DataColumnSidecar>> ReadSidecarsAsync(Stream stream, int maxSidecars) =>
            ReadSidecarChunksAsync(stream, maxSidecars, ProtocolId);
    }
}
