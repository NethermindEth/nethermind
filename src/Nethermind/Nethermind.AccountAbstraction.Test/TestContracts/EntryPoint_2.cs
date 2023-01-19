// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.AccountAbstraction.Test.TestContracts
{
    public sealed class EntryPoint_2 : CallableContract
    {
        public EntryPoint_2(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress)
            : base(transactionProcessor, abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
        }
    }
}
