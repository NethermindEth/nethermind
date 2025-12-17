// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Blockchain.Filters
{
    public class AddressFilter
    {
        public static readonly AddressFilter AnyAddress = new([]);

        private Bloom.BloomExtract[]? _addressesBloomIndexes;

        public AddressFilter(Address address) : this([address])
        {
        }

        public AddressFilter(IEnumerable<AddressAsKey> addresses)
        {
            Addresses = addresses.ToHashSet();
        }

        public HashSet<AddressAsKey> Addresses { get; }
        private Bloom.BloomExtract[] AddressesBloomExtracts => _addressesBloomIndexes ??= CalculateBloomExtracts();

        public bool Accepts(Address address) => Addresses.Count == 0 || Addresses.Contains(address);

        public bool Accepts(ref AddressStructRef address)
        {
            if (Addresses.Count > 0)
            {
                foreach (var a in Addresses)
                {
                    if (a == address) return true;
                }

                return false;
            }

            return true;
        }

        public bool Matches(Bloom bloom)
        {
            if (AddressesBloomExtracts.Length == 0)
            {
                return true;
            }
            foreach (Bloom.BloomExtract index in AddressesBloomExtracts)
            {
                if (bloom.Matches(index))
                {
                    return true;
                }
            }

            return false;
        }

        public bool Matches(ref BloomStructRef bloom)
        {
            if (AddressesBloomExtracts.Length == 0)
            {
                return true;
            }
            foreach (Bloom.BloomExtract index in AddressesBloomExtracts)
            {
                if (bloom.Matches(index))
                {
                    return true;
                }
            }

            return false;
        }

        private Bloom.BloomExtract[] CalculateBloomExtracts() => Addresses.Select(static a => Bloom.GetExtract(a)).ToArray();
    }
}
