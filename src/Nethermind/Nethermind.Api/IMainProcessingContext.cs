// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Api;

public interface IMainProcessingContext
{
    ITransactionProcessor TransactionProcessor { get; }
    IBlockProcessor MainBlockProcessor { get; }
}

public class MaatProcessingContext(ITransactionProcessor transactionProcessor, IBlockProcessor processor) : IMainProcessingContext
{
    public ITransactionProcessor TransactionProcessor { get; } = transactionProcessor;
    public IBlockProcessor MainBlockProcessor { get; } = processor;
}
