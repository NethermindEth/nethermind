// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public interface IReadOnlyTxProcessorSource
    {
        IReadOnlyTxProcessingScope Build(Hash256 stateRoot);
    }

    public interface IReadOnlyTxProcessingScope: IDisposable
    {
        ITransactionProcessor TransactionProcessor { get; }
        IWorldState WorldState { get; }
    }
}
