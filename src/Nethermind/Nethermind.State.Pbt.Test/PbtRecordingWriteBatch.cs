// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.Test;

/// <summary>A batch reserved from <paramref name="buffer"/> that hands each of its records to <paramref name="onRecord"/> when disposed.</summary>
/// <remarks>For test stores that keep groups by path: the records stay in the buffer, but the stores serve folds by path.</remarks>
public sealed class PbtRecordingWriteBatch(IPbtGroupRecordBuffer buffer, int alignedTotalLength, Action<PbtGroupRecord> onRecord) : IPbtGroupRecordWriteBatch
{
    private readonly IPbtGroupRecordWriteBatch _batch = buffer.StartWriteBatch(alignedTotalLength);

    public ulong BasePointer => _batch.BasePointer;

    public Span<byte> Bytes => _batch.Bytes;

    public void Dispose()
    {
        _batch.Dispose();
        for (int offset = 0; offset < alignedTotalLength;)
        {
            if (!buffer.TryLocate(BasePointer + (ulong)offset, out PbtGroupRecord record))
                throw new InvalidOperationException("A written PBT group record is missing from its buffer.");
            onRecord(record);
            offset += PbtGroupRecordCodec.AlignedLength(record.Span.Length);
        }
    }
}
