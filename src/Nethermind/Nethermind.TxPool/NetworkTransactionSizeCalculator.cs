// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool;

public class NetworkTransactionSizeCalculator(TxDecoder txDecoder) : ITransactionSizeCalculator
{
    private readonly TxDecoder _txDecoder = txDecoder;

    public int GetLength(Transaction tx) => _txDecoder.GetLength(tx, RlpBehaviors.SkipTypedWrapping);
}
