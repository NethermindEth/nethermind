// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History.Proofs;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class ContractRootCheck(ISortedKeyValueStore accountHistory, HistoryRowFormat rowFormat, MismatchSink sink) : ViewObserver
{
    private const int IdentityLength = CommitmentKeyLayout.IdentityLength;
    private const int AccountRowKeyLength = Hash256.Size + sizeof(ulong);

    private HistoryRowCursor? _rows;
    private bool _hasRow;
    private ValueHash256 _previous;

    public void Begin(in ValueHash256 identity, ulong from, ulong to, CancellationToken token)
    {
        _rows = null;
        _hasRow = false;
        _previous = Keccak.EmptyTreeHash.ValueHash256;
        if (!TryResolvePath(identity, out ValueHash256 path)) return;

        _rows = new HistoryRowCursor(accountHistory, rowFormat, path.Bytes, from, to, token);
        if (_rows.TryReadStart(out _, out byte[] start)) _previous = HistoryRowScanner.StorageRootOf(start);
        _hasRow = _rows.MoveNext();
    }

    public override void OnChanged(ulong block, in NodeView view) => OnRoot(block, view.Hash);

    public void OnRoot(ulong block, in ValueHash256 rebuilt)
    {
        while (_hasRow && _rows!.Block < block) ConsumeRowOnly();

        if (!_hasRow || _rows!.Block != block)
        {
            sink.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingAccountRow, rebuilt, default));
            return;
        }

        ReadOnlySpan<byte> value = _rows.Value;
        ValueHash256 recorded = HistoryRowScanner.StorageRootOf(value);
        if (!value.IsEmpty && recorded != rebuilt) sink.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.StorageRoot, rebuilt, recorded));
        _previous = recorded;
        _hasRow = _rows.MoveNext();
    }

    public void End()
    {
        while (_hasRow) ConsumeRowOnly();
        _rows = null;
    }

    private void ConsumeRowOnly()
    {
        ulong block = _rows!.Block;
        ValueHash256 recorded = HistoryRowScanner.StorageRootOf(_rows.Value);
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
}
