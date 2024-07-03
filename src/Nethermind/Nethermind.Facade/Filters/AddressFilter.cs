// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Db;

namespace Nethermind.Blockchain.Filters
{
    public class AddressFilter
    {
        public static readonly AddressFilter AnyAddress = new(addresses: new HashSet<AddressAsKey>());

        private Bloom.BloomExtract[]? _addressesBloomIndexes;
        private Bloom.BloomExtract? _addressBloomExtract;
        public static readonly IEnumerable<long> Any = [-1];

        public IEnumerable<long> GetBlockNumbersFrom(LogIndexStorage logIndexStorage)
        {
            if (Addresses is not null)
            {
                var blocks = Addresses.Select(a => logIndexStorage.GetBlocksForAddress(a));
                IEnumerator<long>[] enumerators = blocks.Select(b => b.GetEnumerator()).ToArray();

                try
                {

                    DictionarySortedSet<long, IEnumerator<long>> transactions = new();

                    for (int i = 0; i < enumerators.Length; i++)
                    {
                        IEnumerator<long> enumerator = enumerators[i];
                        if (enumerator.MoveNext())
                        {
                            transactions.Add(enumerator.Current!, enumerator);
                        }
                    }


                    while (transactions.Count > 0)
                    {
                        (long blockNumber, IEnumerator<long> enumerator) = transactions.Min;

                        transactions.Remove(blockNumber);
                        bool isRepeated = false;

                        if (transactions.Count > 0)
                        {
                            (long blockNumber2, IEnumerator<long> enumerator2) = transactions.Min;
                            isRepeated = blockNumber == blockNumber2;
                        }

                        if (enumerator.MoveNext())
                        {

                            transactions.Add(enumerator.Current!, enumerator);
                        }

                        if (!isRepeated)
                        {
                            yield return blockNumber;
                        }

                    }

                }
                finally
                {

                    for (int i = 0; i < enumerators.Length; i++)
                    {
                        enumerators[i].Dispose();
                    }
                }
                yield break;
            }
            if (Address is null)
            {
                yield return Any.First();
                yield break;
            }
            yield return logIndexStorage.GetBlocksForAddress(Address).First();
        }

        public AddressFilter(Address address)
        {
            Address = address;
        }

        public AddressFilter(HashSet<AddressAsKey> addresses)
        {
            Addresses = addresses;
        }

        public Address? Address { get; }
        public HashSet<AddressAsKey>? Addresses { get; }
        private Bloom.BloomExtract[] AddressesBloomExtracts => _addressesBloomIndexes ??= CalculateBloomExtracts();
        private Bloom.BloomExtract AddressBloomExtract => _addressBloomExtract ??= Bloom.GetExtract(Address);

        public bool Accepts(Address address)
        {
            if (Addresses?.Count > 0)
            {
                return Addresses.Contains(address);
            }

            return Address is null || Address == address;
        }

        public bool Accepts(ref AddressStructRef address)
        {
            if (Addresses?.Count > 0)
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

        private Bloom.BloomExtract[] CalculateBloomExtracts() => Addresses.Select(a => Bloom.GetExtract(a)).ToArray();
    }
}
