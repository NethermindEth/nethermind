// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Test.IO;

/// <inheritdoc />
/// <summary>
/// A stream whose async writes and flushes never complete synchronously.
/// </summary>
public sealed class AsyncCompletingStream : MemoryStream
{
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        await base.WriteAsync(buffer, cancellationToken);
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        await base.FlushAsync(cancellationToken);
    }
}
