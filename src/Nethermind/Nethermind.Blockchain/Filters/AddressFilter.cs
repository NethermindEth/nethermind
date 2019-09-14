/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Blockchain.Filters
{
    public class AddressFilter
    {
        public static AddressFilter AnyAddress = new AddressFilter((Address)null);
        
        private Bloom.BloomExtract[] _addressesBloomIndexes;
        private Bloom.BloomExtract? _addressBloomExtract;
        
        public AddressFilter(Address address)
        {
            Address = address;
        }
        
        public AddressFilter(HashSet<Address> addresses)
        {
            Addresses = addresses;
        }
        
        public Address Address { get; set; }
        public HashSet<Address> Addresses { get; set; }
        private Bloom.BloomExtract[] AddressesBloomExtracts => _addressesBloomIndexes ?? (_addressesBloomIndexes = CalculateBloomExtracts());
        private Bloom.BloomExtract AddressBloomExtract => (_addressBloomExtract ?? (_addressBloomExtract = Bloom.GetExtract(Address))).Value;

        public bool Accepts(Address address)
        {
            if (Addresses != null)
            {
                return Addresses.Contains(address);
            }

            return Address == null || Address == address;
        }

        public bool Matches(Bloom bloom)
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

        private Bloom.BloomExtract[] CalculateBloomExtracts() => Addresses.Select(Bloom.GetExtract).ToArray();
    }
}