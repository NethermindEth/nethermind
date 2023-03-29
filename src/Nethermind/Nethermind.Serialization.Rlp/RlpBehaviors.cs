// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Rlp
{
    [Flags]
    public enum RlpBehaviors
    {
        None,
        AllowExtraBytes = 1,
        ForSealing = 2,
        Storage = 4,
        Eip658Receipts = 8,
        AllowUnsigned = 16,
        SkipTypedWrapping = 32, // introduced after typed transactions. In the network (devp2p) transaction has additional wrapping
                                // when we're calculating tx hash or sending raw transaction we should skip this wrapping
                                // with additional wrapping for typed transactions we're decoding Uint8Array([TransactionType, TransactionPayload]
                                // without wrapping we're decoding (TransactionType || TransactionPayload)
        LegacyReceipts = 64,
        All = AllowExtraBytes | ForSealing | Storage | Eip658Receipts | AllowUnsigned | SkipTypedWrapping
    }
}
