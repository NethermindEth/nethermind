// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Merge.AuRa.Contracts;

public interface IWithdrawalContract
{
    void ExecuteWithdrawals(BlockHeader blockHeader, UInt256 failedMaxCount,
        IList<ulong> amounts, IList<Address> addresses, IWorldState worldState);
}
