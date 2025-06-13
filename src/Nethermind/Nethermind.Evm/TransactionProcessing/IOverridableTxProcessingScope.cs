// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing;

// TODO: Separate this from ReadOnlyTxProcessingScope
public interface IOverridableTxProcessingScope : IReadOnlyTxProcessingScope
{
    IOverridableCodeInfoRepository CodeInfoRepository { get; }
    IStateReader StateReader { get; }
}
