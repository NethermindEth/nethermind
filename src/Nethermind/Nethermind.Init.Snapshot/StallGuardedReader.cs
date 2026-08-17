// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Init.Snapshot;

internal sealed class StallGuardedReader(TimeSpan stallTimeout, CancellationToken cancellationToken) : IDisposable
{
    private CancellationTokenSource _stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    public async Task<int> ReadAsync(Stream content, Memory<byte> buffer)
    {
        _stallCts.CancelAfter(stallTimeout);
        try
        {
            int read = await content.ReadAsync(buffer, _stallCts.Token).ConfigureAwait(false);
            if (!_stallCts.TryReset())
                Recreate();
            return read;
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            Recreate();
            throw new IOException($"No data received for {stallTimeout.TotalSeconds}s.", e);
        }
    }

    public void Dispose() => _stallCts.Dispose();

    private void Recreate()
    {
        _stallCts.Dispose();
        _stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }
}
