// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Facade.Filters
{
    public class AddressFilter
    {
        public static readonly AddressFilter AnyAddress = new([]);

        private const int LinearScanMaxCount = 8;

        private readonly HashSet<AddressAsKey>.AlternateLookup<ReadOnlySpan<byte>> _lookup;
        private Bloom.BloomExtract[]? _addressesBloomIndexes;

        public AddressFilter(HashSet<AddressAsKey> addresses)
        {
            if (addresses.Count > LinearScanMaxCount)
            {
                if (addresses.Comparer is not AddressAsKeyComparer)
                {
                    addresses = new HashSet<AddressAsKey>(addresses, AddressAsKeyComparer.Instance);
                }

                _lookup = addresses.GetAlternateLookup<ReadOnlySpan<byte>>();
            }

            Addresses = addresses;
        }

        public AddressFilter(Address address) : this([address])
        {
        }

        /// <summary>Creates an address set that a filter of <paramref name="count"/> addresses uses without a copy.</summary>
        public static HashSet<AddressAsKey> CreateAddressSet(int count) =>
            count > LinearScanMaxCount ? new HashSet<AddressAsKey>(count, AddressAsKeyComparer.Instance) : new HashSet<AddressAsKey>(count);

        public HashSet<AddressAsKey> Addresses { get; }
        private Bloom.BloomExtract[] AddressesBloomExtracts => _addressesBloomIndexes ??= CalculateBloomExtracts();

        public bool Accepts(Address address) => Addresses.Count == 0 || Addresses.Contains(address);

        public bool Accepts(ref AddressStructRef address)
        {
            if (Addresses.Count == 0)
            {
                return true;
            }

            if (_lookup.Set is not null)
            {
                return _lookup.Contains(address.Bytes);
            }

            foreach (AddressAsKey a in Addresses)
            {
                if (a == address) return true;
            }

            return false;

        }

        public bool Matches(Bloom bloom)
        {
            if (AddressesBloomExtracts.Length == 0)
            {
                return true;
            }

            for (int i = 0; i < AddressesBloomExtracts.Length; i++)
            {
                if (bloom.Matches(AddressesBloomExtracts[i]))
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

            for (int i = 0; i < AddressesBloomExtracts.Length; i++)
            {
                if (bloom.Matches(AddressesBloomExtracts[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private Bloom.BloomExtract[] CalculateBloomExtracts() => Addresses.Select(static a => Bloom.GetExtract(a)).ToArray();
    }
}
