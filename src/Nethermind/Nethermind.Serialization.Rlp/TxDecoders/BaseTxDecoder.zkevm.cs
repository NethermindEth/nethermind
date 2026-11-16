// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp.TxDecoders;

public abstract partial class BaseTxDecoder
{
    /// <summary>Never compute the hash at decoding.</summary>
    /// <remarks>
    /// A valid block reads no transaction hash in the guest, and the guest decodes from memory it keeps for the
    /// whole run, so the delayed hash only stores a slice of that memory.
    /// </remarks>
    private const int MaxDelayedHashTxnSize = int.MaxValue;
}
