// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

public static class PipeReaderExtensions
{
    public static async Task<ReadResult> ReadToEndAsync(this PipeReader reader)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync().ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (buffer.IsEmpty && result.IsCompleted)
            {
                return result;
            }
            reader.AdvanceTo(buffer.End);
        }
    }
}
