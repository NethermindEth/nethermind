// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Data
{
    public struct AccessListForRpc
    {
        public AccessListForRpc(IEnumerable<AccessListItemForRpc> accessList, in UInt256 gasUsed)
        {
            AccessList = accessList;
            GasUsed = gasUsed;
        }

        public IEnumerable<AccessListItemForRpc> AccessList { get; }

        public UInt256 GasUsed { get; }
    }
}
