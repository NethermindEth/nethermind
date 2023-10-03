// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Merge.AuRa.Shutter;

public class ValidatorRegistryContract : CallableContract, IValidatorRegistryContract
{
    public ValidatorRegistryContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
    }

    public void Deregister(BlockHeader blockHeader, byte[] message, byte[] signature)
    {
        throw new NotImplementedException();
    }

    public void Register(BlockHeader blockHeader, byte[] message, byte[] signature)
    {
        throw new NotImplementedException();
    }
}
