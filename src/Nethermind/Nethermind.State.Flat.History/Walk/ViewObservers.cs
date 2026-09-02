// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.History.Proofs;

namespace Nethermind.State.Flat.History.Walk;

internal abstract class ViewObserver
{
    public virtual bool ObservesEveryBlock => false;

    public virtual bool OnBlock(ulong block, in NodeView view) => true;

    public virtual void OnChanged(ulong block, in NodeView view)
    {
    }
}

internal sealed class RootHeaderCheck(IHistoryHeaderSource headers, IDb availableBlocks, MismatchSink sink, ILogger logger) : ViewObserver
{
    public ulong Compared { get; private set; }

    public override bool ObservesEveryBlock => true;

    public override bool OnBlock(ulong block, in NodeView view)
    {
        ValueHash256 rebuilt = view.Hash;
        ValueHash256? expected = headers.TryGetStateRoot(block);
        if (expected is null)
        {
            sink.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingHeader, rebuilt, default));
            return false;
        }

        Compared++;

        Span<byte> markerKey = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(markerKey, block);
        byte[]? marker = availableBlocks.Get(markerKey);
        if (marker is not { Length: Hash256.Size } || new ValueHash256(marker) != expected.Value)
        {
            sink.Add(new HistoryWalkMismatch(
                block, HistoryWalkMismatchKind.CapturedMarker, marker is { Length: Hash256.Size } ? new ValueHash256(marker) : default, expected.Value));
        }

        if (rebuilt == expected.Value) return true;

        sink.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.StateRoot, rebuilt, expected.Value));
        if (logger.IsWarn) logger.Warn($"History walk diverged from the header at block {block}; stopping the comparison there.");
        return false;
    }
}

internal sealed class ContractRootCheck(ISortedKeyValueStore accountHistory, HistoryRowFormat rowFormat, MismatchSink sink) : ViewObserver
{
    private const int IdentityLength = CommitmentKeyLayout.IdentityLength;
    private const int AccountRowKeyLength = Hash256.Size + sizeof(ulong);

    private IEnumerator<(ulong Block, byte[] Value)>? _rows;
    private bool _hasRow;
    private ValueHash256 _previous;

    public void Begin(in ValueHash256 identity, ulong from, ulong to)
    {
        _rows = null;
        _hasRow = false;
        _previous = Keccak.EmptyTreeHash.ValueHash256;
        if (!TryResolvePath(identity, out ValueHash256 path)) return;

        HistoryRowCursor cursor = new(accountHistory, rowFormat, path.Bytes, from, to);
        if (cursor.TryReadStart(out _, out byte[] start) && start.Length > 0) _previous = StorageRootOf(start);

        _rows = cursor.Ascending().GetEnumerator();
        _hasRow = _rows.MoveNext();
    }

    public override void OnChanged(ulong block, in NodeView view) => OnRoot(block, view.Hash);

    public void OnRoot(ulong block, in ValueHash256 rebuilt)
    {
        while (_hasRow && _rows!.Current.Block < block) ConsumeRowOnly();

        if (!_hasRow || _rows!.Current.Block != block)
        {
            sink.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingAccountRow, rebuilt, default));
            return;
        }

        byte[] value = _rows.Current.Value;
        if (value.Length == 0)
        {
            _previous = Keccak.EmptyTreeHash.ValueHash256;
        }
        else
        {
            ValueHash256 recorded = StorageRootOf(value);
            if (recorded != rebuilt) sink.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.StorageRoot, rebuilt, recorded));
            _previous = recorded;
        }

        _hasRow = _rows.MoveNext();
    }

    public void End()
    {
        while (_hasRow) ConsumeRowOnly();
        _rows?.Dispose();
        _rows = null;
    }

    private void ConsumeRowOnly()
    {
        (ulong block, byte[] value) = _rows!.Current;
        ValueHash256 recorded = value.Length == 0 ? Keccak.EmptyTreeHash.ValueHash256 : StorageRootOf(value);
        if (recorded != _previous) sink.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingSlotHistory, _previous, recorded));
        _previous = recorded;
        _hasRow = _rows.MoveNext();
    }

    private bool TryResolvePath(in ValueHash256 identity, out ValueHash256 path)
    {
        Span<byte> lower = stackalloc byte[AccountRowKeyLength];
        lower.Clear();
        identity.Bytes[..IdentityLength].CopyTo(lower);
        Span<byte> upper = stackalloc byte[AccountRowKeyLength + 1];
        upper.Fill(0xFF);
        identity.Bytes[..IdentityLength].CopyTo(upper);
        upper[^1] = 0x00;

        using ISortedView view = accountHistory.GetViewBetween(lower, upper);
        while (view.MoveNext())
        {
            if (view.CurrentKey.Length != AccountRowKeyLength) continue;

            path = new ValueHash256(view.CurrentKey[..Hash256.Size]);
            return true;
        }

        path = default;
        return false;
    }

    public static ValueHash256 StorageRootOf(ReadOnlySpan<byte> accountRow)
    {
        RlpReader reader = new(accountRow);
        if (!AccountDecoder.Slim.TryDecodeStruct(ref reader, out AccountStruct account))
        {
            throw new InvalidOperationException("An account history row failed to decode; the column is corrupt.");
        }

        return account.StorageRoot;
    }
}
