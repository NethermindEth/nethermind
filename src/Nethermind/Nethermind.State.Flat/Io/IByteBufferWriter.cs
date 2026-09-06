// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Io;

public interface IByteBufferWriter
{
    Span<byte> GetSpan(int sizeHint);
    void Advance(int count);
    long Written { get; }

    /// <summary>
    /// Smallest writer-local offset (in the same coordinate system as
    /// <see cref="Written"/>) that maps to a 4 KiB-aligned byte in the writer's
    /// eventual destination. Callers can pad to the next 4 KiB boundary with
    /// <c>(-(Written - FirstOffset)) &amp; PageLayout.PageMask</c>. For writers whose backing
    /// destination has no inherent alignment (e.g. transient in-memory buffers),
    /// implementations may return <c>0</c>.
    /// </summary>
    long FirstOffset { get; }

    static void Copy<TWriter>(ref TWriter writer, ReadOnlySpan<byte> value) where TWriter : IByteBufferWriter
    {
        while (value.Length > 0)
        {
            int chunk = Math.Min(value.Length, 256);
            value[..chunk].CopyTo(writer.GetSpan(chunk));
            writer.Advance(chunk);
            value = value[chunk..];
        }
    }
}

