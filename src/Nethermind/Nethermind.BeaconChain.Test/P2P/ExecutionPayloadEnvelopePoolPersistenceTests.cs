// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P;

// gloas/p2p-interface.md ExecutionPayloadEnvelopesByRange/ByRoot: a node serves envelopes for the whole block-request window,
// so a verified envelope must outlive the in-memory cache and the process.
public class ExecutionPayloadEnvelopePoolPersistenceTests
{
    private static readonly ulong Base = FirstGloasSlot + 10;

    [Test]
    [CancelAfter(30_000)]
    public async Task Added_envelope_is_served_by_root_and_by_range_after_a_restart_or_an_eviction([Values] bool restart, CancellationToken token)
    {
        EnvelopeChain chain = new();
        (Hash256 genesis, Hash256 genesisHash) = chain.Put(Base, Hash256.Zero, Hash256.Zero);
        (Hash256 onChain, Hash256 onChainHash) = chain.Put(Base + 1, genesis, genesisHash);
        (Hash256 fork, _) = chain.Put(Base + 1, genesis, genesisHash, salt: 1);
        (Hash256 head, _) = chain.Put(Base + 2, onChain, onChainHash);
        chain.SetHead(head, Base + 2);
        Hash256[] added = [genesis, onChain, fork, head];

        ExecutionPayloadEnvelopePool pool;
        if (restart)
        {
            chain.AddEnvelopes(added);
            pool = new ExecutionPayloadEnvelopePool(store: chain.Store, status: chain.Status);
        }
        else
        {
            pool = new ExecutionPayloadEnvelopePool(capacity: 1, store: chain.Store, status: chain.Status);
            foreach (Hash256 root in added)
            {
                pool.Add(root, chain.Envelope(root));
            }
        }

        List<ResponseChunk> byRoot = await ReqRespTestChannel.ReadResponseAsync(new ExecutionPayloadEnvelopesByRootProtocol(EnvelopeChain.Spec, pool).ListenAsync,
            ExecutionPayloadEnvelopeRoots.Encode(new ExecutionPayloadEnvelopeRoots { Roots = [.. added, Keccak.Compute("never added")] }), token);
        List<ResponseChunk> byRange = await ReqRespTestChannel.ReadResponseAsync(new ExecutionPayloadEnvelopesByRangeProtocol(EnvelopeChain.Spec, pool).ListenAsync,
            ExecutionPayloadEnvelopesByRangeRequest.Encode(new ExecutionPayloadEnvelopesByRangeRequest { StartSlot = Base, Count = 3 }), token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(ServedRoots(byRoot), Is.EqualTo(added), "every added envelope, the fork and the head included");
        Assert.That(ServedRoots(byRange), Is.EqualTo(new[] { genesis, onChain }), "the head chain only, without the head, as before");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Unreadable_stored_envelope_is_not_served_until_added_again(
        [Values(BeaconChainStoreEnvelopeTests.Corruption.NotSnappy, BeaconChainStoreEnvelopeTests.Corruption.LengthNear2GiB,
            BeaconChainStoreEnvelopeTests.Corruption.LengthAboveInt32, BeaconChainStoreEnvelopeTests.Corruption.Length128MiB)]
        BeaconChainStoreEnvelopeTests.Corruption corruption,
        CancellationToken token)
    {
        MemColumnsDb<BeaconChainDbColumns> db = new();
        MemDb column = (MemDb)db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes);
        BeaconChainStore store = new(db, Sepolia);
        SignedExecutionPayloadEnvelope corrupt = BeaconChainStoreEnvelopeTests.Envelope(FirstGloasSlot + 1);
        SignedExecutionPayloadEnvelope intact = BeaconChainStoreEnvelopeTests.Envelope(FirstGloasSlot + 2);
        Hash256 corruptRoot = corrupt.Message!.BeaconBlockRoot!;
        Hash256 intactRoot = intact.Message!.BeaconBlockRoot!;
        ExecutionPayloadEnvelopePool before = new(store: store);
        before.Add(corruptRoot, corrupt);
        before.Add(intactRoot, intact);
        db.GetColumnDb(BeaconChainDbColumns.ExecutionPayloadEnvelopes)[corruptRoot.Bytes] = BeaconChainStoreEnvelopeTests.CorruptRecord(corruption, SignedExecutionPayloadEnvelope.Encode(corrupt));

        ExecutionPayloadEnvelopePool after = new(capacity: 1, store: store);
        byte[] request = ExecutionPayloadEnvelopeRoots.Encode(new ExecutionPayloadEnvelopeRoots { Roots = [corruptRoot, intactRoot] });
        ExecutionPayloadEnvelopesByRootProtocol protocol = new(Sepolia, after);
        List<ResponseChunk> unreadable = await ReqRespTestChannel.ReadResponseAsync(protocol.ListenAsync, request, token);
        long readsBefore = column.ReadsCount;
        bool servedOnRepeat = after.TryGet(corruptRoot, out _);
        long repeatReads = column.ReadsCount - readsBefore;
        after.Add(corruptRoot, corrupt);
        after.Add(intactRoot, intact);
        bool servedOnceAdded = after.TryGet(corruptRoot, out _);
        List<ResponseChunk> readded = await ReqRespTestChannel.ReadResponseAsync(new ExecutionPayloadEnvelopesByRootProtocol(Sepolia, new ExecutionPayloadEnvelopePool(store: store)).ListenAsync, request, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(ServedRoots(unreadable), Is.EqualTo(new[] { intactRoot }), "the unreadable record is refused and the rest still served");
        Assert.That(servedOnRepeat, Is.False, "an unreadable record stays refused");
        Assert.That(repeatReads, Is.Zero, "an unreadable record is not read again, so each request does not allocate its claimed length again");
        Assert.That(servedOnceAdded, Is.True, "adding the envelope again lifts the refusal, also once it is evicted from memory");
        Assert.That(ServedRoots(readded), Is.EqualTo(new[] { corruptRoot, intactRoot }), "adding it again replaces the unreadable record");
    }

    [Test]
    public void Envelope_without_a_payload_or_under_a_root_it_does_not_name_is_refused_with_or_without_a_store([Values] bool withStore, [Values] bool namesAnotherBlock)
    {
        ExecutionPayloadEnvelopePool pool = new(store: withStore ? new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia) : null);
        SignedExecutionPayloadEnvelope envelope = BeaconChainStoreEnvelopeTests.Envelope(FirstGloasSlot + 1);
        Hash256 root = namesAnotherBlock ? Keccak.Compute("another block") : envelope.Message!.BeaconBlockRoot!;
        if (!namesAnotherBlock)
        {
            envelope.Message!.Payload = null;
        }

        Assert.Throws<ArgumentException>(() => pool.Add(root, envelope));
        Assert.That(pool.TryGet(root, out _), Is.False, "nothing is held");
    }

    private static Hash256[] ServedRoots(List<ResponseChunk> chunks) =>
    [
        .. chunks.Select(static c =>
        {
            Assert.That(c.Result, Is.EqualTo(ReqRespFraming.ResponseCode.Success));
            SignedExecutionPayloadEnvelope.Decode(c.Payload, out SignedExecutionPayloadEnvelope envelope);
            return envelope.Message!.BeaconBlockRoot!;
        }),
    ];

}
