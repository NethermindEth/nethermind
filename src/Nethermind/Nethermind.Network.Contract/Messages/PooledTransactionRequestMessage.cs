// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Contract.Messages;

public readonly struct PooledTransactionRequestMessage : IResourceRequestMessage<PooledTransactionRequestMessage, ValueHash256>
{
    public ValueHash256 TxHash { get; init; }

    public static PooledTransactionRequestMessage From(ValueHash256 txHash) => new() { TxHash = txHash };
}
