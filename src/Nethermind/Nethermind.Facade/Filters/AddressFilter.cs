// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Blockchain.Filters
{
    public class AddressFilter(HashSet<AddressAsKey> addresses)
    {
        public static readonly AddressFilter AnyAddress = new([]);

        private Bloom.BloomExtract[]? _addressesBloomIndexes;

        public AddressFilter(Address address) : this([address])
        {
        }

        public HashSet<AddressAsKey> Addresses { get; } = addresses;
        private Bloom.BloomExtract[] AddressesBloomExtracts => _addressesBloomIndexes ??= CalculateBloomExtracts();

        public bool Accepts(Address address) => Addresses.Count == 0 || Addresses.Contains(address);

        public bool Accepts(ref AddressStructRef address)
        {
            if (Addresses.Count > 0)
            {
                foreach (AddressAsKey a in Addresses)
                {
                    if (a == address) return true;
                }

                return false;
            }

            return true;
        }

        public bool Matches(Bloom bloom)
        {
            bool result = true;
            Bloom.BloomExtract[]? indexes = AddressesBloomExtracts;
            for (int i = 0; i < indexes.Length; i++)
            {
                result = bloom.Matches(indexes[i]);
                if (result)
                {
                    break;
                }
            }

            return result;
        }

        public bool Matches(ref BloomStructRef bloom)
        {
            bool result = true;
            Bloom.BloomExtract[]? indexes = AddressesBloomExtracts;
            for (int i = 0; i < indexes.Length; i++)
            {
                result = bloom.Matches(indexes[i]);
                if (result)
                {
                    break;
                }
            }

            return result;
        }

        private Bloom.BloomExtract[] CalculateBloomExtracts() => Addresses.Select(static a => Bloom.GetExtract(a)).ToArray();
    }
}
