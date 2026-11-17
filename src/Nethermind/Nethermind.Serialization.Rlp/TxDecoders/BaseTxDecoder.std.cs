// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp.TxDecoders;

public abstract partial class BaseTxDecoder
{
    /// <summary>The encoded size above which decoding computes the hash at once instead of keeping the bytes for later.</summary>
    private const int MaxDelayedHashTxnSize = 32768;
}
