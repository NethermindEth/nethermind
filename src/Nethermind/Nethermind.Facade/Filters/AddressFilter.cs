// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            if (Addresses is not null)
            {
                return Addresses.Contains(address);
            }

            return Address is null || Address == address;
        }

        public bool Accepts(ref AddressStructRef address)
        {
            if (Addresses is not null)
            {
                foreach (var a in Addresses)
                {
                    if (a == address) return true;
                }

                return false;
            }

            return Address is null || Address == address;
        }

        public bool Matches(Core.Bloom bloom)
        {
            if (Addresses is not null)
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
            else if (Address is null)
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
            if (Addresses is not null)
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
            else if (Address is null)
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
