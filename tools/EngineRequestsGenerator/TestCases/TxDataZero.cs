// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public static class TxDataZero
{
    public static Transaction[] GetTxs(PrivateKey privateKey, int nonce, long blockGasConsumptionTarget)
    {   long numberOfBytes = (blockGasConsumptionTarget - GasCostOf.Transaction) / GasCostOf.TxDataZero;
        byte[] data = new byte[numberOfBytes];

        return
        [
            Build.A.Transaction
            .WithNonce((UInt256)nonce)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithTo(TestItem.AddressB)
            .WithChainId(BlockchainIds.Holesky)
            .WithData(data)
            .WithGasLimit(blockGasConsumptionTarget)
            .SignedAndResolved(privateKey)
            .TestObject
        ];
    }
}
