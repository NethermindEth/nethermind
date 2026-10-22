// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

public static class PipeReaderExtensions
{
    public static async Task<ReadResult> ReadToEndAsync(this PipeReader reader, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsCompleted || result.IsCanceled)
            {
                return result;
            }

            // Separate method to shrink the async state machine by not including
            // the ReadOnlySequence<byte> buffer in the main method
            AdvanceReaderToEnd(reader, in result);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void AdvanceReaderToEnd(PipeReader reader, in ReadResult result)
        {
            // Extract buffer reading to a separate method to reduce async state machine size
            ReadOnlySequence<byte> buffer = result.Buffer;
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }
}
