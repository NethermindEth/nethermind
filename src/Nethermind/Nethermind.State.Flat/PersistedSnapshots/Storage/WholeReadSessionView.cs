// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Cached mmap-view coordinates for a single open <see cref="WholeReadSession"/>: a raw
/// pointer + length pair, captured once at merge setup so the per-merge helpers can
/// construct <see cref="WholeReadSessionReader"/> instances on demand without paying the
/// per-call <see cref="ObjectDisposedException"/> check on the session.
/// </summary>
/// <remarks>
/// Pointer lifetime is owned by the originating session — the caller must ensure the
/// session is not disposed while any view derived from it is in use. This is the same
/// contract as <see cref="WholeReadSession.GetView"/> / <see cref="WholeReadSessionReader"/>.
/// </remarks>
public readonly unsafe struct WholeReadSessionView(IntPtr ptr, long length)
    : IHsstReaderSource<WholeReadSessionReader, NoOpPin>
{
    public IntPtr Ptr => ptr;
    public long Length => length;

    /// <summary>Materialise a fresh reader over this view.</summary>
    public WholeReadSessionReader CreateReader() => new((byte*)ptr, length);
}
