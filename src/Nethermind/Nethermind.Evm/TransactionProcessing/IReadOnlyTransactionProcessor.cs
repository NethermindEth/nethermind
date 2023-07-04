// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Evm.TransactionProcessing
{
    public interface IReadOnlyTransactionProcessor : ITransactionProcessor, IDisposable
    {
        bool IsContractDeployed(Address address);
    }
}
