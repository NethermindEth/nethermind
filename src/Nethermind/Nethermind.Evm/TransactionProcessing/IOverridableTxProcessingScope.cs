// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing;

public interface IOverridableTxProcessingScope : IDisposable
{
    IWorldState WorldState { get; }
    IOverridableCodeInfoRepository CodeInfoRepository { get; }
    ITransactionProcessor TransactionProcessor { get; }
}
