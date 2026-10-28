// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Keeps the overlay of a block being traced so that tracing its transactions one by one folds each
/// changeset once rather than once per transaction. An overlay another request still holds is never extended under
/// it: that request gets the block folded fresh instead. A prefix with a row missing is not lent at all.
/// The least recently rented block makes room for a new one, and its overlay is folded again for the new block when no
/// request holds it, so a run of traces over different blocks keeps folding into the same few overlays.</summary>
internal sealed class MidBlockOverlayCache(TransactionChangesetStore store, int blocks)
{
    public const int DefaultBlocks = 4;

    private readonly MidBlockOverlay?[] _overlays = new MidBlockOverlay?[Math.Max(1, blocks)];
    private readonly long[] _rentedAt = new long[Math.Max(1, blocks)];
    private long _rents;
    private readonly Lock _lock = new();

    public MidBlockOverlayCache(TransactionChangesetStore store) : this(store, DefaultBlocks)
    {
    }

    /// <summary>The cache lock covers only the slot bookkeeping; the fold, which reads the column, runs outside it
    /// on an overlay nobody else can see mid-fold, so a cold block being folded never holds up a trace of another.</summary>
    public bool TryRent(ulong block, in ValueHash256 hash, ushort beforeTransaction, out Lease lease, long version)
    {
        MidBlockOverlay overlay;
        lock (_lock)
        {
            int entry = IndexOf(block);
            MidBlockOverlay? cached = entry < 0 ? null : _overlays[entry];
            // The hash, not the height: the rows of a height can be replaced by a sibling's between two rents, and a
            // prefix folded from the one it replaced is not a prefix of this block at all.
            bool shareable = cached is not null && cached.Hash == hash && cached.Version == version && !cached.Extending
                && cached.Folded <= beforeTransaction && (cached.Folded == beforeTransaction || cached.Pins == 0);
            if (shareable)
            {
                overlay = cached!;
            }
            else
            {
                if (entry < 0) entry = LeastRecentlyRented();
                // The overlay in the entry, evicted or of this same block, is folded again when no request holds it;
                // one a request still reads is left to that request.
                MidBlockOverlay? current = _overlays[entry];
                overlay = current is { Pins: 0, Extending: false } ? current : new MidBlockOverlay();
                overlay.Reset(block, hash);
                overlay.Version = version;
                _overlays[entry] = overlay;
            }

            _rentedAt[entry] = ++_rents;
            overlay.Pins++;
            overlay.Extending = overlay.Folded < beforeTransaction;
        }

        if (overlay.Extending && !TryExtend(overlay, beforeTransaction))
        {
            lease = default;
            return false;
        }

        lock (_lock)
        {
            overlay.Extending = false;
            if (overlay.Folded == beforeTransaction)
            {
                lease = new Lease(this, overlay);
                return true;
            }

            overlay.Pins--;
            lease = default;
            return false;
        }
    }

    /// <summary>A fold that fails is a refusal, not a failure, whatever failed: a row the codec cannot read, a column
    /// that could not be read, a store closing under the request. The trace then replays the prefix and answers, which
    /// is what every other refusal here does, and the pin and the extending flag go back so the entry is lent again.</summary>
    private bool TryExtend(MidBlockOverlay overlay, ushort beforeTransaction)
    {
        try
        {
            Extend(overlay, beforeTransaction);
            return true;
        }
        catch (Exception exception)
        {
            if (exception is InvalidDataException) Flat.Metrics.RecordUnreadableTransactionChangesetRow();

            lock (_lock)
            {
                // The fold applies a row's entries as it reads them, so a row that throws part way through leaves the
                // overlay holding part of that transaction's own writes while still reporting the boundary before it.
                // Discarding what it holds keeps the next rent at that boundary a refusal, rather than a prefix that
                // already contains the target. The pin is this thread's: an overlay is only extended when it was
                // selected with none.
                overlay.Reset(overlay.Block, overlay.Hash);
                overlay.Extending = false;
                overlay.Pins--;
            }

            return false;
        }
    }

    private int IndexOf(ulong block)
    {
        for (int i = 0; i < _overlays.Length; i++)
        {
            if (_overlays[i] is { } overlay && overlay.Block == block) return i;
        }

        return -1;
    }

    private int LeastRecentlyRented()
    {
        int oldest = 0;
        for (int i = 0; i < _overlays.Length; i++)
        {
            if (_overlays[i] is null) return i;
            if (_rentedAt[i] < _rentedAt[oldest]) oldest = i;
        }

        return oldest;
    }

    private void Return(MidBlockOverlay overlay)
    {
        lock (_lock)
        {
            overlay.Pins--;
        }
    }

    private void Extend(MidBlockOverlay overlay, ushort beforeTransaction)
    {
        if (overlay.Folded >= beforeTransaction) return;

        using ISortedView view = store.OpenBetween(overlay.Block, overlay.Folded, beforeTransaction);
        while (view.MoveNext())
        {
            if (!ChangesetKeyLayout.IsRowKey(view.CurrentKey)) continue;
            if (ChangesetKeyLayout.TransactionIndexOf(view.CurrentKey) != overlay.Folded) return;

            overlay.Fold(overlay.Folded, view.CurrentValue);
        }
    }

    internal readonly struct Lease(MidBlockOverlayCache cache, MidBlockOverlay overlay) : IDisposable
    {
        public MidBlockOverlay Overlay { get; } = overlay;

        public void Dispose() => cache.Return(Overlay);
    }
}
