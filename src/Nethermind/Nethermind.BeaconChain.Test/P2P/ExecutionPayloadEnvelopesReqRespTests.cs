// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

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

    public enum ByRootProtocol
    {
        Blocks,
        DataColumnSidecars,
        ExecutionPayloadEnvelopes,
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task By_root_listen_answers_an_empty_root_list_with_no_chunks([Values] ByRootProtocol kind, CancellationToken token)
    {
        Func<IChannel, ISessionContext, Task> listen = kind switch
        {
            ByRootProtocol.Blocks => new BeaconBlocksByRootProtocolV2(Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>())).ListenAsync,
            ByRootProtocol.DataColumnSidecars => new DataColumnSidecarsByRootProtocol(Spec, new DataColumnSidecarPool()).ListenAsync,
            ByRootProtocol.ExecutionPayloadEnvelopes => new ExecutionPayloadEnvelopesByRootProtocol(Spec, new ExecutionPayloadEnvelopePool()).ListenAsync,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        byte[] emptyRoots = ExecutionPayloadEnvelopeRoots.Encode(new ExecutionPayloadEnvelopeRoots { Roots = [] });

        Assert.That(await ListenAsync(listen, emptyRoots, token), Is.Empty);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Gossip_envelope_is_served_by_root_only_once_added_as_verified(CancellationToken token)
    {
        EnvelopeChain chain = new();
        ulong slot = FirstGloasSlot + 1;
        (Hash256 root, _) = chain.Put(slot, Hash256.Zero, Hash256.Zero);
        SlotClock clock = new(EnvelopeChain.Spec, new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(EnvelopeChain.Spec.GenesisTime + slot * EnvelopeChain.Spec.SecondsPerSlot + 6)));
        GossipRouter router = new(EnvelopeChain.Spec, clock, LimboLogs.Instance, chain.Store, chain.Status);
        int raised = 0;
        router.ExecutionPayloadEnvelopeReceived += _ => raised++;
        ExecutionPayloadEnvelopesByRootProtocol protocol = new(EnvelopeChain.Spec, chain.Pool);
        byte[] request = ExecutionPayloadEnvelopeRoots.Encode(new ExecutionPayloadEnvelopeRoots { Roots = [root] });

        MessageValidity validity = router.Handle(GossipTopics.ExecutionPayload, gloasTopic: true, Snappy.CompressToArray(SignedExecutionPayloadEnvelope.Encode(chain.Envelope(root))));
        List<ResponseChunk> beforeVerified = await ListenAsync(protocol.ListenAsync, request, token);
        chain.AddEnvelopes(root);
        List<ResponseChunk> afterVerified = await ListenAsync(protocol.ListenAsync, request, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((validity, raised), Is.EqualTo((MessageValidity.Ignored, 1)), "the gossip envelope passes every check but its signature and is consumed");
            Assert.That(beforeVerified, Is.Empty, "an envelope whose signature is unchecked is not served");
            Assert.That(afterVerified.Select(static c => c.ContextBytes), Is.EqualTo(new[] { ForkDigest.Compute(EnvelopeChain.Spec, EnvelopeChain.Spec.GetEpoch(slot)) }), "served once verified");
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task By_range_listen_serves_the_head_chain_and_clamps_the_count(CancellationToken token)
    {
        EnvelopeChain chain = new();
        ulong start = FirstGloasSlot + 1;
        (Hash256 parent, Hash256 parentHash) = chain.Put(start, Hash256.Zero, Hash256.Zero);
        (Hash256 fork, _) = chain.Put(start + 1, parent, parentHash, salt: 1);
        (Hash256 tip, Hash256 tipHash) = chain.Put(start + 2, parent, parentHash);
        List<(Hash256 Root, ulong Slot)> onChain = [(parent, start), (tip, start + 2)];
        for (ulong slot = start + 3; onChain.Count <= (int)ExecutionPayloadEnvelopesProtocolBase.MaxRequestPayloads; slot++)
        {
            (tip, tipHash) = chain.Put(slot, tip, tipHash);
            onChain.Add((tip, slot));
        }

        chain.AddEnvelopes([.. onChain.Select(static b => b.Root), fork]);
        chain.SetHead(tip, onChain[^1].Slot);
        ExecutionPayloadEnvelopesByRangeProtocol protocol = new(EnvelopeChain.Spec, chain.Pool);

        List<ResponseChunk> chunks = await ListenAsync(protocol.ListenAsync,
            ExecutionPayloadEnvelopesByRangeRequest.Encode(new ExecutionPayloadEnvelopesByRangeRequest { StartSlot = start, Count = ulong.MaxValue }), token);

        Assert.That(chunks.Select(static c => { SignedExecutionPayloadEnvelope.Decode(c.Payload, out SignedExecutionPayloadEnvelope e); return e.Message!.BeaconBlockRoot; }),
            Is.EqualTo(onChain.Where(b => b.Slot < start + ExecutionPayloadEnvelopesProtocolBase.MaxRequestPayloads).Select(static b => b.Root)),
            "the fork is skipped, and the count is clamped to the cap while the chain runs past it");
    }

    public enum RangeFault
    {
        UnreadableStoredBlock,
        ZeroCount,
    }

    [TestCase(RangeFault.UnreadableStoredBlock, ReqRespFraming.ResponseCode.ServerError, 0L)]
    [TestCase(RangeFault.ZeroCount, ReqRespFraming.ResponseCode.InvalidRequest, 1L)]
    [CancelAfter(30_000)]
    public async Task By_range_listen_records_a_peer_failure_only_for_an_invalid_request(RangeFault fault, byte expected, long recorded, CancellationToken token)
    {
        EnvelopeChain chain = new();
        ulong start = FirstGloasSlot + 1;
        (Hash256 head, _) = chain.Put(start, Hash256.Zero, Hash256.Zero);
        chain.SetHead(head, start);
        if (fault == RangeFault.UnreadableStoredBlock)
        {
            chain.Corrupt(head);
        }

        ExecutionPayloadEnvelopesByRangeProtocol protocol = new(EnvelopeChain.Spec, chain.Pool);
        long before = FailureCount(protocol.Id, ReqRespFailureReason.InvalidMessage);

        List<ResponseChunk> chunks = await ListenAsync(protocol.ListenAsync, ExecutionPayloadEnvelopesByRangeRequest.Encode(
            new ExecutionPayloadEnvelopesByRangeRequest { StartSlot = start, Count = fault == RangeFault.ZeroCount ? 0UL : 1UL }), token);

        Assert.That((chunks.Single().Result, FailureCount(protocol.Id, ReqRespFailureReason.InvalidMessage) - before), Is.EqualTo((expected, recorded)),
            "only a fault of the requesting peer is recorded against it");
    }

    // The libp2p host closes the response stream once the handler returns, even when it faults; the dial side reads until then.
    private static async Task<List<ResponseChunk>> ListenAsync(Func<IChannel, ISessionContext, Task> listen, byte[] requestSsz, CancellationToken token)
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new Nethermind.Libp2p.Core.State());
        Channel channel = new();
        Task listening = Task.Run(async () =>
        {
            try
            {
                await listen(channel.Reverse, context);
            }
            finally
            {
                await channel.Reverse.WriteEofAsync(token);
            }
        }, token);
        Stream stream = new ChannelStreamAdapter(channel);
        await ReqRespFraming.WriteRequestAsync(stream, requestSsz, token);
        await channel.WriteEofAsync(token);

        List<ResponseChunk> chunks = [];
        while (await ReqRespFraming.ReadResponseChunkAsync(stream, ReqRespFraming.ForkContextLength, ReqRespFraming.MaxPayloadSize, token) is { } chunk)
        {
            chunks.Add(chunk);
        }

        await listening.WaitAsync(token);
        return chunks;
    }

    private static long FailureCount(string protocolId, ReqRespFailureReason reason) =>
        Metrics.BeaconChainReqRespFailures.TryGetValue(new ReqRespFailureKey(protocolId, reason), out long count) ? count : 0;

    /// <summary>Exposes the protected chunked-response reader for direct testing.</summary>
    private sealed class TestExecutionPayloadEnvelopesProtocol(BeaconChainSpec spec) : ExecutionPayloadEnvelopesProtocolBase(spec)
    {
        public const string ProtocolId = "/test/execution-payload-envelopes-limits/1";

        public async Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> ReadEnvelopesAsync(Stream response, int maxEnvelopes)
        {
            RequestTiming timing = new();
            IReadOnlyList<SignedExecutionPayloadEnvelope> envelopes = await ReadEnvelopeChunksAsync(response, maxEnvelopes, ProtocolId, timing: timing);
            Assert.That(timing.Chunks, Is.EqualTo(envelopes.Count));
            return envelopes;
        }
    }
}
