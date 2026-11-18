// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Tracing.ParityStyle;

/// <summary>Bounds a streamed trace response that is kept whole in memory instead of being sent as it is written.</summary>
public sealed class TraceOutputLimit(long limit)
{
    public long Limit => limit;

    /// <summary>Bytes of the response written to the pipe directly, past the JSON writer.</summary>
    public long BypassedBytes { get; set; }

    /// <param name="writtenBytes">Bytes written through the JSON writer, committed and pending.</param>
    public void Check(long writtenBytes)
    {
        if (writtenBytes + BypassedBytes > limit) throw new TraceDataLimitExceededException(limit);
    }
}
