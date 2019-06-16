using System;
using Nethermind.Core;

namespace Nethermind.DataMarketplace.Core.Events
{
    public class AddressChangedEventArgs : EventArgs
    {
        public Address OldAddress { get; }
        public Address NewAddress { get; }

        public AddressChangedEventArgs(Address oldAddress, Address newAddress)
        {
            OldAddress = oldAddress;
            NewAddress = newAddress;
        }
    }
}