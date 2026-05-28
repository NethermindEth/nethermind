// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery.Discv5.Messages;

internal sealed class Discv5MessageBuffer(int length) : IDisposable
{
    private byte[]? _buffer = SafeArrayPool<byte>.Shared.Rent(length);

    public Span<byte> Span => _buffer.AsSpan(0, Length);

    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, Length);

    public int Length { get; } = length;

    public void Dispose()
    {
        if (_buffer is not null)
        {
            SafeArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }
}
