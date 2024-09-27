// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth;

public interface IFromTransaction<out T> : ITxTyped where T : TransactionForRpc
{
    static abstract T FromTransaction(Transaction tx, TransactionConverterExtraData extraData);
}

public readonly struct TransactionConverterExtraData
{
    public ulong? ChainId { get; init; }
    public Hash256? BlockHash { get; init; }
    public long? BlockNumber { get; init; }
    public int? TxIndex { get; init; }
    public UInt256? BaseFee { get; init; }
    public TxReceipt Receipt { get; init; }
}
