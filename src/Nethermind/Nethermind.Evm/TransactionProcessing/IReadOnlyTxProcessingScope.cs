// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing;

public interface IReadOnlyTxProcessingScope : IDisposable
{
    IOverridableCodeInfoRepository CodeInfoRepository { get; }
    IStateReader StateReader { get; }
    ITransactionProcessor TransactionProcessor { get; }
    IWorldState WorldState { get; }
}
