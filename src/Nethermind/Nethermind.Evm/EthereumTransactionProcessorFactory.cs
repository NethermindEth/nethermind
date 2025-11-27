// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Evm;

public class EthereumTransactionProcessorFactory : ITransactionProcessorFactory
{
    public ITransactionProcessor Create(ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator, ISpecProvider specProvider, IWorldState state, IVirtualMachine vm, ICodeInfoRepository codeInfoRepository, ILogManager logManager)
    {
        return new TransactionProcessor(blobBaseFeeCalculator, specProvider, state, vm, codeInfoRepository, logManager);
    }
}
