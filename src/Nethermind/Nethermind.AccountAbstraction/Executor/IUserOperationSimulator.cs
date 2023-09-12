// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc;

namespace Nethermind.AccountAbstraction.Executor
{
    public interface IUserOperationSimulator
    {
        ResultWrapper<Keccak> Simulate(UserOperation userOperation,
            BlockHeader parent,
            UInt256? timestamp = null,
            CancellationToken cancellationToken = default);

        CallOutput EstimateGas(BlockHeader header, Transaction tx,
            CancellationToken cancellationToken);
    }
}
