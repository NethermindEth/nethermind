// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Core.Test.Encoding;

/// <summary>Hand-builds eth/63 receipt RLP, so a log count can be declared without materialising the logs.</summary>
public static class ReceiptRlpBuilder
{
    /// <param name="logCount">Number of items written into the receipt's log sequence.</param>
    /// <param name="log">
    /// Log to repeat, or <c>null</c> for one-byte <c>0xC0</c> placeholders - the cheapest item a
    /// count can be built from, and one no decoder accepts as a log.
    /// </param>
    public static byte[] EncodeReceipt(int logCount, LogEntry? log = null)
    {
        int logLength = log is null ? Rlp.OfEmptyList.Length : Rlp.LengthOf(log);
        int logsLength = logCount * logLength;
        int contentLength = Rlp.LengthOf((byte)1)
                            + Rlp.LengthOf(0UL)
                            + Rlp.LengthOf(Bloom.Empty)
                            + Rlp.LengthOfSequence(logsLength);

        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(bytes);
        writer.StartSequence(contentLength);
        writer.Encode((byte)1);
        writer.Encode(0UL);
        writer.Encode(Bloom.Empty);
        writer.StartSequence(logsLength);
        for (int i = 0; i < logCount; i++)
        {
            if (log is null)
            {
                writer.StartSequence(0);
            }
            else
            {
                LogEntryDecoder.Instance.Encode(ref writer, log);
            }
        }

        return bytes;
    }

    /// <summary>The smallest log the decoder accepts: an address, no topics and no data.</summary>
    public static LogEntry MinimalLog() => new(Address.Zero, [], []);
}
