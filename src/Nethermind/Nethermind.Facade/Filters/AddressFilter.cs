// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Blockchain.Filters
{
    public class AddressFilter
    {
        public static readonly AddressFilter AnyAddress = new(addresses: new HashSet<Address>());

        private Bloom.BloomExtract[]? _addressesBloomIndexes;
        private Bloom.BloomExtract? _addressBloomExtract;

        public AddressFilter(Address address)
        {
            Address = address;
        }

        public AddressFilter(HashSet<Address> addresses)
        {
            Addresses = addresses;
        }

        private Address? Address { get; }
        private HashSet<Address>? Addresses { get; }
        private Bloom.BloomExtract[] AddressesBloomExtracts => _addressesBloomIndexes ??= CalculateBloomExtracts();
        private Bloom.BloomExtract AddressBloomExtract => _addressBloomExtract ??= Bloom.GetExtract(Address);

        public bool Accepts(Address address)
        {
            if (Addresses is not null && Addresses.Count > 0)
            {
                return Addresses.Contains(address);
            }

            return Address is null || Address == address;
        }

        public bool Accepts(ref AddressStructRef address)
        {
            if (Addresses is not null && Addresses.Count > 0)
            {
                foreach (var a in Addresses)
                {
                    if (a == address) return true;
                }

                return false;
            }

            return Address is null || Address == address;
        }

        public bool Matches(Bloom bloom)
        {
            if (Addresses is not null)
            {
                bool result = true;
                var indexes = AddressesBloomExtracts;
                for (var i = 0; i < indexes.Length; i++)
                {
                    result = bloom.Matches(in indexes[i]);
                    if (result)
                    {
                        break;
                    }
                }

                return result;
            }
            if (Address is null)
            {
                return true;
            }
            return bloom.Matches(AddressBloomExtract);
        }

        public bool Matches(ref BloomStructRef bloom)
        {
            if (Addresses is not null)
            {
                bool result = true;
                var indexes = AddressesBloomExtracts;
                for (var i = 0; i < indexes.Length; i++)
                {
                    result = bloom.Matches(in indexes[i]);
                    if (result)
                    {
                        break;
                    }
                }

                return result;
            }
            if (Address is null)
            {
                return true;
            }
            return bloom.Matches(AddressBloomExtract);
        }

        private Bloom.BloomExtract[] CalculateBloomExtracts() => Addresses.Select(Bloom.GetExtract).ToArray();
    }
}
