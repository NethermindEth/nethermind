// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

// TODO: Separate this from ReadOnlyTxProcessingScope
public interface IOverridableTxProcessingScope : IReadOnlyTxProcessingScope
{
    IOverridableCodeInfoRepository CodeInfoRepository { get; }
    IStateReader StateReader { get; }
}
