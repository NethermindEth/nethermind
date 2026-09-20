// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// Framing and DoS-limit tests for the Gloas execution payload envelope req/resp protocols,
/// mirroring <c>ReqRespLimitsTests</c>' pattern for the block protocols.
/// </summary>
public class ExecutionPayloadEnvelopesReqRespTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    private static SignedExecutionPayloadEnvelope Envelope(ulong slot, ulong builderIndex = 3) => new()
    {
        Message = new ExecutionPayloadEnvelope
        {
            Payload = new ExecutionPayloadGloas { SlotNumber = slot },
            ExecutionRequests = new ExecutionRequestsGloas(),
            BuilderIndex = builderIndex,
            BeaconBlockRoot = new Hash256([.. BitConverter.GetBytes(slot), .. new byte[24]]),
            ParentBeaconBlockRoot = Hash256.Zero,
        },
        Signature = new BlsSignature(new byte[BlsSignature.Length]),
    };

    private static Task WriteEnvelopeChunkAsync(Stream stream, SignedExecutionPayloadEnvelope envelope)
    {
        byte[] contextBytes = ForkDigest.Compute(Spec, Spec.GetEpoch(envelope.Message!.Payload!.SlotNumber));
        return ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, contextBytes, SignedExecutionPayloadEnvelope.Encode(envelope), default);
    }

    [Test]
    public async Task Response_exceeding_the_chunk_limit_is_rejected_and_the_stream_closed()
    {
        const int maxEnvelopes = 3;
        TestExecutionPayloadEnvelopesProtocol protocol = new(Spec);

        using MemoryStream stream = new();
        for (int i = 0; i < maxEnvelopes + 2; i++)
        {
            await WriteEnvelopeChunkAsync(stream, Envelope(4_000 + (ulong)i));
        }

        stream.Position = 0;

        long before = FailureCount(TestExecutionPayloadEnvelopesProtocol.ProtocolId, ReqRespFailureReason.LimitExceeded);

        Eth2ReqRespException? thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() => protocol.ReadEnvelopesAsync(stream, maxEnvelopes));
        Assert.That(thrown!.Message, Does.Contain(maxEnvelopes.ToString()));
        Assert.That(FailureCount(TestExecutionPayloadEnvelopesProtocol.ProtocolId, ReqRespFailureReason.LimitExceeded), Is.EqualTo(before + 1), "limit violation recorded");
        Assert.That(stream.Position, Is.LessThan(stream.Length), "stream was closed to the peer before it was fully drained");
    }

    [Test]
    public async Task Response_at_exactly_the_chunk_limit_is_accepted()
    {
        const int maxEnvelopes = 3;
        TestExecutionPayloadEnvelopesProtocol protocol = new(Spec);

        using MemoryStream stream = new();
        for (int i = 0; i < maxEnvelopes; i++)
        {
            await WriteEnvelopeChunkAsync(stream, Envelope(5_000 + (ulong)i));
        }

        stream.Position = 0;

        IReadOnlyList<SignedExecutionPayloadEnvelope> envelopes = await protocol.ReadEnvelopesAsync(stream, maxEnvelopes);
        Assert.That(envelopes, Has.Count.EqualTo(maxEnvelopes));
    }

    [Test]
    public void DialAsync_rejects_more_roots_than_MaxRequestPayloads_before_writing_to_the_wire()
    {
        ExecutionPayloadEnvelopesByRootProtocol protocol = new(Spec, new ExecutionPayloadEnvelopePool());
        Hash256[] roots = new Hash256[ExecutionPayloadEnvelopesProtocolBase.MaxRequestPayloads + 1];
        for (int i = 0; i < roots.Length; i++)
        {
            roots[i] = Hash256.Zero;
        }

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => protocol.DialAsync(null!, null!, roots));
    }

    private static long FailureCount(string protocolId, ReqRespFailureReason reason) =>
        Metrics.BeaconChainReqRespFailures.TryGetValue(new ReqRespFailureKey(protocolId, reason), out long count) ? count : 0;

    /// <summary>Exposes the protected chunked-response reader for direct testing.</summary>
    private sealed class TestExecutionPayloadEnvelopesProtocol(BeaconChainSpec spec) : ExecutionPayloadEnvelopesProtocolBase(spec)
    {
        public const string ProtocolId = "/test/execution-payload-envelopes-limits/1";

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> ReadEnvelopesAsync(Stream stream, int maxEnvelopes) =>
            ReadEnvelopeChunksAsync(stream, maxEnvelopes, ProtocolId);
    }
}
