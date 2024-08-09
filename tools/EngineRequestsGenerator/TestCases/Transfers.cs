// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace EngineRequestsGenerator.TestCases;

public static class Transfers
{
    public static Transaction[] GetTxs(PrivateKey privateKey, int nonce)
    {
        return
        [
            Build.A.Transaction
            .WithNonce((UInt256)nonce)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithTo(TestItem.AddressB)
            .WithChainId(BlockchainIds.Holesky)
            .SignedAndResolved(privateKey)
            .TestObject
        ];
    }
}
