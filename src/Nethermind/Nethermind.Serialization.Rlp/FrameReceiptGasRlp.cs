// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp;

internal static class FrameReceiptGasRlp
{
    /// <summary>
    /// Decodes a stored frame receipt's <c>gas_used</c>, tolerating both the current
    /// <c>[execution, state]</c> list and the pre-2D scalar written by devnet7.
    /// </summary>
    /// <remarks>
    /// An RLP scalar sits below the sequence-prefix range, so it is unambiguous from the list.
    /// The scalar path attributes the whole value to execution (<c>state = 0</c>); read-compatibility
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
