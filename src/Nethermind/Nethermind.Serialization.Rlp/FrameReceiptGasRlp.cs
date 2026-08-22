// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp;

internal static class FrameReceiptGasRlp
{
    /// <summary>
    /// Decodes a stored frame receipt's <c>gas_used</c>, tolerating both the current
    /// <c>[execution, state]</c> list and the pre-two-dimensional scalar written by
    /// <c>eip8141-frame-txs-devnet7</c>.
    /// </summary>
    /// <remarks>
    /// A <see cref="ulong"/> RLP scalar is always below the sequence prefix range, so the list is
    /// unambiguously distinguished from a scalar. The scalar path attributes the whole value to the
    /// execution dimension (<c>state = 0</c>): it preserves the per-frame total (and therefore
    /// <c>GasUsedTotal</c> and the JSON-RPC <c>gasUsed</c>), but the receipt is not wire-faithful when
    /// re-encoded for <c>GetReceipts</c>, where the split is carried on the wire. Read-compatibility
    /// only, removable once no node holds devnet7-era frame-tx receipts on disk.
    /// </remarks>
    public static void DecodeGasUsed(ref RlpReader reader, out ulong execution, out ulong state)
    {
        if (reader.IsSequenceNext())
        {
            int gasUsedEnd = reader.ReadSequenceLength() + reader.Position;
            execution = reader.DecodeULong();
            state = reader.DecodeULong();
            reader.Check(gasUsedEnd);
        }
        else
        {
            execution = reader.DecodeULong();
            state = 0;
        }
    }
}
