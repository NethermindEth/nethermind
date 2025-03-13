// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing;

public interface IOverridableTxProcessingScope : IReadOnlyTxProcessingScope
{
    IOverridableCodeInfoRepository CodeInfoRepository { get; }
}
