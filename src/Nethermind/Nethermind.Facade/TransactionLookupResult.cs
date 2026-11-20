// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Facade.Eth;

namespace Nethermind.Facade;

public readonly struct TransactionLookupResult(Transaction? transaction, in TransactionForRpcContext extraData, ShardBlobNetworkWrapper? networkWrapper)
{
    public Transaction? Transaction { get; } = transaction;
    public TransactionForRpcContext ExtraData { get; } = extraData;

    /// <summary>The blob sidecar a pending transaction travels with in the mempool; <see langword="null"/> for a canonical one.</summary>
    public ShardBlobNetworkWrapper? NetworkWrapper { get; } = networkWrapper;
}
