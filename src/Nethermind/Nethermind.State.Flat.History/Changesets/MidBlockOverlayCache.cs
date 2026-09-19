// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Keeps the overlay of a block being traced so that tracing its transactions one by one folds each
/// changeset once rather than once per transaction. An overlay another request still holds is never extended under
/// it: that request gets the block folded fresh instead. A prefix with a row missing is not lent at all.</summary>
internal sealed class MidBlockOverlayCache(TransactionChangesetStore store, int blocks)
{
    public const int DefaultBlocks = 4;

    private readonly LruCache<ulong, MidBlockOverlay> _overlays = new(Math.Max(1, blocks), nameof(MidBlockOverlayCache));
    private readonly Lock _lock = new();

    public MidBlockOverlayCache(TransactionChangesetStore store) : this(store, DefaultBlocks)
    {
    }

    /// <summary>The cache lock covers only the slot bookkeeping; the fold, which reads the column, runs outside it
    /// on an overlay nobody else can see mid-fold, so a cold block being folded never holds up a trace of another.</summary>
    public bool TryRent(ulong block, in ValueHash256 hash, ushort beforeTransaction, out Lease lease, long version = 0)
    {
        MidBlockOverlay overlay;
        lock (_lock)
        {
            MidBlockOverlay? cached = _overlays.Get(block);
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
                overlay = new MidBlockOverlay();
                overlay.Reset(block, hash);
                overlay.Version = version;
                _overlays.Set(block, overlay);
            }

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

    /// <summary>A row the codec cannot read is a refusal, not a failure: the trace then replays the prefix and
    /// answers, which is what every other refusal here does. The pin goes back with it.</summary>
    private bool TryExtend(MidBlockOverlay overlay, ushort beforeTransaction)
    {
        try
        {
            Extend(overlay, beforeTransaction);
            return true;
        }
        catch (InvalidDataException)
        {
            Flat.Metrics.RecordUnreadableTransactionChangesetRow();

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
