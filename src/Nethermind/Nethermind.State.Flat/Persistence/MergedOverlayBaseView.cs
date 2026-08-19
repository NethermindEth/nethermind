// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Ascending merge of the RocksDB overlay with the base shard tables over the same key range:
/// on equal keys the overlay wins, and overlay tombstones (see <see cref="BaseTableStore.OverlayTombstone"/>)
/// are consumed silently — they shadow the base record without being surfaced.
/// </summary>
/// <remarks>
/// This is both the iterator surface of the arena base persistence (wrapped by the existing
/// account/storage iterators) and the record source for a shard fold — folding is exactly "materialize
/// this merge", which is what guarantees fold and reads agree byte-for-byte.
/// </remarks>
internal sealed class MergedOverlayBaseView(ISortedView overlay, BaseShardCursor baseCursor) : ISortedView
{
    private bool _overlayValid;
    private bool _baseValid;
    private bool _started;
    private bool _currentIsOverlay;
    // Equal keys: the base record was shadowed by the overlay's, so both sides advance together.
    private bool _currentConsumedBase;

    public bool StartBefore(ReadOnlySpan<byte> value) =>
        throw new NotSupportedException($"{nameof(MergedOverlayBaseView)} is forward-only.");

    public bool MoveNext()
    {
        if (!_started)
        {
            _started = true;
            _overlayValid = overlay.MoveNext();
            _baseValid = baseCursor.MoveNext();
        }
        else if (_currentIsOverlay)
        {
            _overlayValid = overlay.MoveNext();
            if (_currentConsumedBase) _baseValid = baseCursor.MoveNext();
        }
        else
        {
            _baseValid = baseCursor.MoveNext();
        }

        while (true)
        {
            if (!_overlayValid)
            {
                _currentIsOverlay = false;
                return _baseValid;
            }

            int cmp = _baseValid ? overlay.CurrentKey.SequenceCompareTo(baseCursor.CurrentKey) : -1;
            if (cmp > 0)
            {
                _currentIsOverlay = false;
                return true;
            }

            bool consumedBase = cmp == 0;
            if (BaseTableStore.IsTombstone(overlay.CurrentValue))
            {
                // A deletion: swallow it together with the base record it shadows.
                _overlayValid = overlay.MoveNext();
                if (consumedBase) _baseValid = baseCursor.MoveNext();
                continue;
            }

            _currentIsOverlay = true;
            _currentConsumedBase = consumedBase;
            return true;
        }
    }

    public ReadOnlySpan<byte> CurrentKey => _currentIsOverlay ? overlay.CurrentKey : baseCursor.CurrentKey;

    public ReadOnlySpan<byte> CurrentValue => _currentIsOverlay ? overlay.CurrentValue : baseCursor.CurrentValue;

    public void Dispose()
    {
        overlay.Dispose();
        baseCursor.Dispose();
    }
}
