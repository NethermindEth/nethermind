// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.ReqResp;

public class DataColumnSidecarsByRangeReadFailureTests
{
    private const ulong First = 13_410_304;
    private const ulong Last = First + 3;
    private const ulong Column = 5;

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private static readonly PeerId Requester = new Identity(privateKey: null, KeyType.Secp256K1).PeerId;

    private static Hash256 RootAt(ulong slot) => Keccak.Compute($"canonical {slot}");

    [Test]
    [CancelAfter(30_000)]
    public async Task A_column_that_cannot_be_read_at_or_above_the_floor_ends_the_reply_with_a_server_error([Values] bool gloas, CancellationToken token)
    {
        (FaultyColumnsDb db, DataColumnSidecarsByRangeProtocol protocol, _, _) = ServerWithStoredColumns(gloas);
        long invalidBefore = InvalidMessageCount(protocol);
        db.FailReads = true;

        List<ResponseChunk> chunks = await RequestAsync(protocol, First, 4, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(chunks, Has.Count.EqualTo(1), "no sidecar could be read, so the reply is the error alone");
        Assert.That(chunks[0].Result, Is.EqualTo(ReqRespFraming.ResponseCode.ServerError));
        Assert.That(InvalidMessageCount(protocol), Is.EqualTo(invalidBefore), "a failing store is not the requester's fault");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Columns_read_before_the_failing_one_are_still_sent_ahead_of_the_error(CancellationToken token)
    {
        (FaultyColumnsDb db, DataColumnSidecarsByRangeProtocol protocol, _, _) = ServerWithStoredColumns(gloas: false, warmSlots: 2);
        db.FailReads = true;

        List<ResponseChunk> chunks = await RequestAsync(protocol, First, 4, token);

        Assert.That(chunks.ConvertAll(static c => c.Result), Is.EqualTo(new[] { ReqRespFraming.ResponseCode.Success, ReqRespFraming.ResponseCode.Success, ReqRespFraming.ResponseCode.ServerError }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_column_the_store_does_not_hold_is_omitted_without_an_error([Values] bool gloas, CancellationToken token)
    {
        (_, DataColumnSidecarsByRangeProtocol protocol, _, _) = ServerWithStoredColumns(gloas);

        List<ResponseChunk> chunks = await RequestAsync(protocol, First, 4, token, columns: [Column + 1]);

        Assert.That(chunks, Is.Empty, "a column this node does not custody is not part of the reply");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_column_that_cannot_be_read_below_the_floor_is_omitted_without_an_error(CancellationToken token)
    {
        (FaultyColumnsDb db, DataColumnSidecarsByRangeProtocol protocol, DataColumnSidecarPool pool, _) = ServerWithStoredColumns(gloas: false, floor: Last + 1);
        Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(Last + 1), "test setup: every requested slot is below the floor");
        db.FailReads = true;

        List<ResponseChunk> chunks = await RequestAsync(protocol, First, 4, token);

        Assert.That(chunks, Is.Empty, "below the floor a shorter reply is allowed");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_column_that_cannot_be_read_at_the_floor_slot_ends_the_reply_with_a_server_error(CancellationToken token)
    {
        (FaultyColumnsDb db, DataColumnSidecarsByRangeProtocol protocol, DataColumnSidecarPool pool, _) = ServerWithStoredColumns(gloas: false, floor: Last);
        Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(Last), "test setup: the last requested slot is the floor");
        db.FailReads = true;

        List<ResponseChunk> chunks = await RequestAsync(protocol, First, 4, token);

        Assert.That(chunks.ConvertAll(static c => c.Result), Is.EqualTo(new[] { ReqRespFraming.ResponseCode.ServerError }), "the floor slot is complete, so its column is not skipped");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_stored_column_that_reads_fine_is_served_at_or_above_the_floor([Values] bool gloas, CancellationToken token)
    {
        (_, DataColumnSidecarsByRangeProtocol protocol, DataColumnSidecarPool pool, _) = ServerWithStoredColumns(gloas);
        Assert.That(pool.EarliestCompletelyServableSlot, Is.LessThanOrEqualTo(First), "test setup: every requested slot is at or above the floor");

        List<ResponseChunk> chunks = await RequestAsync(protocol, First, 4, token);

        Assert.That(chunks.ConvertAll(static c => c.Result), Is.EqualTo(new[] { ReqRespFraming.ResponseCode.Success, ReqRespFraming.ResponseCode.Success, ReqRespFraming.ResponseCode.Success, ReqRespFraming.ResponseCode.Success }));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_column_stored_after_the_pool_missed_it_is_served_not_failed([Values] bool gloas, CancellationToken token)
    {
        (FaultyColumnsDb db, DataColumnSidecarsByRangeProtocol protocol, _, BeaconChainStore store) = ServerWithStoredColumns(gloas, stored: [Column + 1]);
        DataColumnSidecarPool writer = new(store: store);
        Action arrive = () => AddColumn(writer, gloas, Column, First);
        db.AfterNextRecordRead = gloas ? () => db.AfterNextRecordRead = arrive : arrive;

        List<ResponseChunk> chunks = await RequestAsync(protocol, First, 1, token);

        Assert.That(chunks.ConvertAll(static c => c.Result), Is.EqualTo(new[] { ReqRespFraming.ResponseCode.Success }), "the column arrived while the reply was being built");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_block_with_an_unreadable_column_sends_none_of_its_columns(CancellationToken token)
    {
        (FaultyColumnsDb db, DataColumnSidecarsByRangeProtocol protocol, _, _) = ServerWithStoredColumns(gloas: false, warmSlots: 1, stored: [Column, Column + 1]);
        db.FailReads = true;

        List<ResponseChunk> chunks = await RequestAsync(protocol, First, 1, token, columns: [Column, Column + 1]);

        Assert.That(chunks.ConvertAll(static c => c.Result), Is.EqualTo(new[] { ReqRespFraming.ResponseCode.ServerError }), "the first column is held in memory, but the block is not sent in part");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_store_that_cannot_say_whether_it_holds_a_column_ends_the_reply_with_a_server_error(CancellationToken token)
    {
        RecordCheckFailingColumnsDb db = new();
        BeaconChainStore store = new(db, Spec);
        store.ApplyCanonicalIndexChanges([(First, RootAt(First))], First);
        store.RaiseDataColumnFloor(First);
        DataColumnSidecarPool pool = new(store: store);
        Assert.That(pool.EarliestCompletelyServableSlot, Is.EqualTo(First), "test setup: the requested slot is at the floor");
        DataColumnSidecarsByRangeProtocol protocol = new(Spec, pool, store);

        List<ResponseChunk> chunks = await RequestAsync(protocol, First, 1, token);

        Assert.That(chunks.ConvertAll(static c => c.Result), Is.EqualTo(new[] { ReqRespFraming.ResponseCode.ServerError }), "a column the store may hold is not skipped");
    }

    private sealed class RecordCheckFailingColumnsDb : IColumnsDb<BeaconChainDbColumns>
    {
        private readonly MemColumnsDb<BeaconChainDbColumns> _inner = new();
        private readonly IDb _sidecars = new KeyExistsFailingDb();

        public IDb GetColumnDb(BeaconChainDbColumns key) => key == BeaconChainDbColumns.DataColumnSidecars ? _sidecars : _inner.GetColumnDb(key);

        public IEnumerable<BeaconChainDbColumns> ColumnKeys => _inner.ColumnKeys;

        public IColumnsWriteBatch<BeaconChainDbColumns> StartWriteBatch() => new InMemoryColumnWriteBatch<BeaconChainDbColumns>(this);

        public IColumnDbSnapshot<BeaconChainDbColumns> CreateSnapshot() => throw new NotSupportedException();

        public void Dispose() { }

        public void Flush(bool onlyWal = false) { }
    }

    private sealed class KeyExistsFailingDb : MemDb, IDb
    {
        public new bool KeyExists(ReadOnlySpan<byte> key) => throw new ObjectDisposedException("the data column table");
    }

    private sealed record Server(FaultyColumnsDb Db, DataColumnSidecarsByRangeProtocol Protocol, DataColumnSidecarPool Pool, BeaconChainStore Store);

    private static Server ServerWithStoredColumns(bool gloas, ulong? floor = null, int warmSlots = 0, ulong[]? stored = null)
    {
        FaultyColumnsDb db = new();
        BeaconChainStore store = new(db, Spec);
        List<(ulong Slot, Hash256? Root)> canonical = [];
        DataColumnSidecarPool writer = new(store: store);
        for (ulong slot = First; slot <= Last; slot++)
        {
            canonical.Add((slot, RootAt(slot)));
            foreach (ulong column in stored ?? [Column])
            {
                AddColumn(writer, gloas, column, slot);
            }
        }

        store.ApplyCanonicalIndexChanges(canonical, Last);
        if (floor is { } raised)
        {
            store.RaiseDataColumnFloor(raised);
        }

        DataColumnSidecarPool pool = new(store: store);
        for (int i = 0; i < warmSlots; i++)
        {
            pool.Add(RootAt(First + (ulong)i), First + (ulong)i, DataColumnSidecarTestFixture.BuildValidSidecar(Column, First + (ulong)i, blobCount: 1));
        }

        return new Server(db, new DataColumnSidecarsByRangeProtocol(Spec, pool, store), pool, store);
    }

    private static void AddColumn(DataColumnSidecarPool pool, bool gloas, ulong column, ulong slot)
    {
        if (gloas)
        {
            pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(column, slot, RootAt(slot)));
        }
        else
        {
            pool.Add(RootAt(slot), slot, DataColumnSidecarTestFixture.BuildValidSidecar(column, slot, blobCount: 1));
        }
    }

    private static async Task<List<ResponseChunk>> RequestAsync(DataColumnSidecarsByRangeProtocol protocol, ulong startSlot, ulong count, CancellationToken token, ulong[]? columns = null)
    {
        Channel channel = new();
        Task listen = Task.Run(async () =>
        {
            await protocol.ListenAsync(channel.Reverse, Context());
            await channel.Reverse.WriteEofAsync();
        }, token);

        ChannelStreamAdapter client = new(channel);
        await ReqRespFraming.WriteRequestAsync(client, DataColumnSidecarsByRangeRequest.Encode(new DataColumnSidecarsByRangeRequest { StartSlot = startSlot, Count = count, Columns = columns ?? [Column] }), token);
        List<ResponseChunk> chunks = [];
        while (await ReqRespFraming.ReadResponseChunkAsync(client, contextBytesLength: 4, ReqRespFraming.MaxPayloadSize, token) is { } chunk)
        {
            chunks.Add(chunk);
        }

        await channel.WriteEofAsync(token);
        await listen;
        return chunks;
    }

    private static long InvalidMessageCount(DataColumnSidecarsByRangeProtocol protocol) =>
        Metrics.BeaconChainReqRespFailures.TryGetValue(new ReqRespFailureKey(protocol.Id, ReqRespFailureReason.InvalidMessage), out long count) ? count : 0;

    private static ISessionContext Context() => ReqRespTestChannel.Context(Requester);
}
