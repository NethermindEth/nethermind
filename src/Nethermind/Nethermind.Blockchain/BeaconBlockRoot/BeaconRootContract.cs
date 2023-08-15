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
using Nethermind.Int256;

public interface IBeaconRootContract 
{
    byte[] Invoke(BlockHeader blockHeader);
}

public sealed class BeaconRootContract : CallableContract, IBeaconRootContract
{
    public BeaconRootContract(ITransactionProcessor transactionProcessor, Address contractAddress)
        : base(transactionProcessor, null, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
    {
    }

    public byte[] Invoke(BlockHeader blockHeader)
    {
        var result = Call(blockHeader, Address.SystemUser, UnlimitedGas);
        return result;
    }
}
