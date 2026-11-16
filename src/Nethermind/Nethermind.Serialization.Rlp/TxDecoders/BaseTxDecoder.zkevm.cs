// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp.TxDecoders;

public abstract partial class BaseTxDecoder
{
    /// <summary>Never compute the hash at decoding.</summary>
    /// <remarks>
    /// A valid block reads no transaction hash in the guest. The guest decodes block bodies from memory it keeps for
    /// the whole run, so the delayed hash stores only a slice of that memory. A decode without a borrowed source
    /// copies the bytes, which still costs much less than keccak in a zkVM.
    /// </remarks>
    private const int MaxDelayedHashTxnSize = int.MaxValue;
}
