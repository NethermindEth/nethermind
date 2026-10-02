// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;

namespace Nethermind.Consensus.ExecutionRequests;

public partial class ExecutionRequestsProcessor
{
    partial void SetSystemCallHashes()
    {
        _withdrawalTransaction.Hash = _withdrawalTransaction.CalculateHash();
        _consolidationTransaction.Hash = _consolidationTransaction.CalculateHash();
        _builderDepositTransaction.Hash = _builderDepositTransaction.CalculateHash();
        _builderExitTransaction.Hash = _builderExitTransaction.CalculateHash();
    }
}
