// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

public interface IReadOnlyTxProcessingEnvFactory
{
    public IReadOnlyTxProcessorSource Create();
}
