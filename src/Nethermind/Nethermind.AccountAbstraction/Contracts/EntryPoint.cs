// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.AccountAbstraction.Contracts
{
    public sealed class EntryPoint : CallableContract
    {
        public EntryPoint(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress)
            : base(transactionProcessor, abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
        }
    }
}
