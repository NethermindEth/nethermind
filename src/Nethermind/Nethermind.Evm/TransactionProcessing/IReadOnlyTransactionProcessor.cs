// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing
{
    public interface IReadOnlyTransactionProcessor : ITransactionProcessor, IDisposable
    {
        IWorldState WorldState { get; set; }
        bool IsContractDeployed(Address address);
    }
}
