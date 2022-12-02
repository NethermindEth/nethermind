// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.DataMarketplace.Core.Events
{
    public class AddressChangedEventArgs : EventArgs
    {
        public Address? OldAddress { get; }
        public Address NewAddress { get; }

        public AddressChangedEventArgs(Address oldAddress, Address newAddress)
        {
            OldAddress = oldAddress;
            NewAddress = newAddress;
        }
    }
}
