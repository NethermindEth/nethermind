// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Facade.Simulate;

public interface ISimulateTransactionProcessorFactory
{
    ITransactionProcessor CreateTransactionProcessor(
        ISpecProvider specProvider,
        IWorldState stateProvider,
        IVirtualMachine virtualMachine,
        ICodeInfoRepository codeInfoRepository,
        ILogManager? logManager,
        bool validate);
}
