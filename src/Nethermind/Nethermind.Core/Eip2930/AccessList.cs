// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Core.Eip2930
{
    public class AccessList
    {
        public AccessList(IReadOnlyDictionary<Address, IReadOnlySet<UInt256>> data,
            Queue<object>? orderQueue = null)
        {
            Data = data;
            OrderQueue = orderQueue;
        }

        public IReadOnlyDictionary<Address, IReadOnlySet<UInt256>> Data { get; }

        /// <summary>
        /// Only used for access lists generated outside of Nethermind
        /// </summary>
        public IReadOnlyCollection<object>? OrderQueue { get; }

        /// <summary>
        /// Has no duplicate entries (allows for more efficient serialization / deserialization)
        /// </summary>
        public bool IsNormalized => OrderQueue is null;
    }
}
