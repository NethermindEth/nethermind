// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing;

public interface IReadOnlyTxProcessingScope : IDisposable
{
    ITransactionProcessor TransactionProcessor { get; }
    IWorldState WorldState { get; }
    void Init(Hash256 stateRoot);
    void Reset() => Dispose();
}
