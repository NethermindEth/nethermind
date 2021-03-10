//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Blockchain.Filters
{
    public class AddressFilter
    {
        public static AddressFilter AnyAddress = new((Address)null);
        
        private Core.Bloom.BloomExtract[] _addressesBloomIndexes;
        private Core.Bloom.BloomExtract? _addressBloomExtract;
        
        public AddressFilter(Address address)
        {
            Address = address;
        }
        
        public AddressFilter(HashSet<Address> addresses)
        {
            Addresses = addresses;
        }
        
        public Address? Address { get; set; }
        public HashSet<Address>? Addresses { get; set; }
        private Core.Bloom.BloomExtract[] AddressesBloomExtracts => _addressesBloomIndexes ??= CalculateBloomExtracts();
        private Core.Bloom.BloomExtract AddressBloomExtract => _addressBloomExtract ??= Core.Bloom.GetExtract(Address);

        public bool Accepts(Address address)
        {
            if (Addresses != null)
            {
                return Addresses.Contains(address);
            }

            return Address == null || Address == address;
        }
        
        public bool Accepts(ref AddressStructRef address)
        {
            if (Addresses != null)
            {
                foreach (var a in Addresses)
                {
                    if (a == address) return true;
                }

                return false;
            }

            return Address == null || Address == address;
        }

        public bool Matches(Core.Bloom bloom)
        {
            if (Addresses != null)
            {
                bool result = true;
                var indexes = AddressesBloomExtracts;
                for (var i = 0; i < indexes.Length; i++)
                {
                    var index = indexes[i];
                    result = bloom.Matches(ref index); 
                    if (result)
                    {
                        break;
                    }
                }

                return result;
            }
            else if (Address == null)
            {
                return true;
            }
            else
            {
                return bloom.Matches(AddressBloomExtract);
            }
        }

        public bool Matches(ref BloomStructRef bloom)
        {
            if (Addresses != null)
            {
                bool result = true;
                var indexes = AddressesBloomExtracts;
                for (var i = 0; i < indexes.Length; i++)
                {
                    var index = indexes[i];
                    result = bloom.Matches(ref index); 
                    if (result)
                    {
                        break;
                    }
                }

                return result;
            }
            else if (Address == null)
            {
                return true;
            }
            else
            {
                return bloom.Matches(AddressBloomExtract);
            }
        }

        private Core.Bloom.BloomExtract[] CalculateBloomExtracts() => Addresses.Select(Core.Bloom.GetExtract).ToArray();
    }
}
