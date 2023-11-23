// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool;

public class NetworkTransactionSizeCalculator : ITransactionSizeCalculator
{
    private readonly TxDecoder _txDecoder;

    public NetworkTransactionSizeCalculator(TxDecoder txDecoder)
    {
        _txDecoder = txDecoder;
    }

    public int GetLength(Transaction tx)
    {
        return _txDecoder.GetLength(tx, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping);
    }
}
