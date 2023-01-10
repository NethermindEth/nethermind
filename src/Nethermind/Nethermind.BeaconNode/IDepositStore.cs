// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core2;
using Nethermind.Core2.Containers;

namespace Nethermind.BeaconNode
{
    public interface IDepositStore
    {
        IList<Deposit> Deposits { get; }

        Deposit Place(DepositData deposit);

        bool Verify(Deposit deposit);

        // TODO: maybe make it internal?
        IMerkleList DepositData { get; set; }
    }
}
