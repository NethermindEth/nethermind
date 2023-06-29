// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Merge.AuRa.Contracts;

public interface IWithdrawalContract
{
    void ExecuteWithdrawals(BlockHeader blockHeader, IList<ulong> amounts, IList<Address> addresses);
}
