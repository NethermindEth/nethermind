// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Blockchain;

public interface IReadOnlyTxProcessingScope : IDisposable
{
    ITransactionProcessor TransactionProcessor { get; }
    IWorldState WorldState { get; }
    void Reset() => Dispose();
}
