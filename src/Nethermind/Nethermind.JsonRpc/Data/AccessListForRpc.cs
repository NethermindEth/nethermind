// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.JsonRpc.Data
{
    public class AccessListForRpc
    {
        public AccessListForRpc(AccessListItemForRpc[] accessList, in UInt256 gasUsed)
        {
            AccessList = accessList;
            GasUsed = gasUsed;
        }

        public AccessListItemForRpc[] AccessList { get; }

        public UInt256 GasUsed { get; }
    }
}
