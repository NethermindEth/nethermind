// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Caching;

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
    public bool TryRent(ulong block, ushort beforeTransaction, out Lease lease)
    {
        MidBlockOverlay overlay;
        lock (_lock)
        {
            MidBlockOverlay? cached = _overlays.Get(block);
            bool shareable = cached is not null && !cached.Extending && cached.Folded <= beforeTransaction && (cached.Folded == beforeTransaction || cached.Pins == 0);
            if (shareable)
            {
                overlay = cached!;
            }
            else
            {
                overlay = new MidBlockOverlay();
                overlay.Reset(block);
                _overlays.Set(block, overlay);
            }

            overlay.Pins++;
            overlay.Extending = overlay.Folded < beforeTransaction;
        }

        if (overlay.Extending) Extend(overlay, beforeTransaction);

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
