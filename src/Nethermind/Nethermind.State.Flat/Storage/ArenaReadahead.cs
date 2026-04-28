// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Implements <see cref="IHsstReadahead"/> by issuing ahead-of-cursor <see cref="ArenaReservation.Touch"/> calls
/// so that subsequent mmap reads hit warm pages.
/// </summary>
internal sealed class ArenaReadahead(
    ArenaReservation reservation,
    int columnOffset,
    int columnLength,
    int windowSize = 1 << 20,
    int lookahead = 256 * 1024) : IHsstReadahead
{
    private int _prefetchedUpTo;

    public void HintPosition(int dataOffset)
    {
        if (dataOffset + lookahead <= _prefetchedUpTo) return;

        int start = _prefetchedUpTo;
        int end = Math.Min(dataOffset + windowSize, columnLength);
        if (start >= end) return;

        reservation.Touch(columnOffset + start, end - start);
        _prefetchedUpTo = end;
    }
}
