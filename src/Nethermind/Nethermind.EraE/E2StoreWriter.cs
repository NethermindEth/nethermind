// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE;

public class E2StoreWriter(Stream stream) : Era1.E2StoreWriter(stream)
{
    internal const int HeaderSize = 8;

    public async Task<int> WriteEntriesFromRawStream(Stream stream, CancellationToken cancellation = default)
    {
        int totalBytesRead = 0;
        // read bytes from the stream until the end and write to this stream
        var buffer = new byte[1024 * 1024];
        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation);
        while (bytesRead > 0)
        {
            totalBytesRead += bytesRead;
            await _stream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellation);
            _checksumCalculator.AppendData(buffer.AsMemory(0, bytesRead).Span);
            bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation);
        }
        return totalBytesRead;
    }
}
