// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Core.Eip2930
{
    /// <summary>
    /// We store the extra information here to be able to recreate the order of the incoming transactions.
    /// EIP-2930 (https://eips.ethereum.org/EIPS/eip-2930) states that:
    /// 'Allowing duplicates
    /// This is done because it maximizes simplicity, avoiding questions of what to prevent duplication against:
    /// just between two addresses/keys in the access list,
    /// between the access list and the tx sender/recipient/newly created contract,
    /// other restrictions?
    /// Because gas is charged per item, there is no gain and only cost in including a value in the access list twice,
    /// so this should not lead to extra chain bloat in practice.'
    ///
    /// While spec is simplified in this matter (somewhat) it leads to a bit more edge cases.
    /// We can no longer simply store the access list as a dictionary, we need to store the order of items
    /// and info on duplicates. The way that I suggest is by adding an additional queue structure.
    /// It be further optimized by only including a queue of integers and a strict ordering algorithm for the dictionary.
    ///
    /// I leave it for later in case such an optimization is needed.
    /// </summary>
    public class AccessListBuilder
    {
        private readonly Dictionary<Address, IReadOnlySet<UInt256>> _data = new();

        private readonly Queue<object> _orderQueue = new();

        private Address? _currentAddress;

        public void AddAddress(Address address)
        {
            _currentAddress = address;
            _orderQueue.Enqueue(_currentAddress);
            if (!_data.ContainsKey(_currentAddress))
            {
                _data[_currentAddress] = new HashSet<UInt256>();
            }
        }

        public void AddStorage(in UInt256 index)
        {
            if (_currentAddress is null)
            {
                throw new InvalidOperationException("No address known when adding index to the access list");
            }

            _orderQueue.Enqueue(index);
            (_data[_currentAddress] as HashSet<UInt256>)!.Add(index);
        }

        public AccessList ToAccessList()
        {
            return new(_data, _orderQueue);
        }
    }
}
