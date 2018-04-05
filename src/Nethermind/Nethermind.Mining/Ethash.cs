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

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

[assembly: InternalsVisibleTo("Nethermind.Mining.Test")]

namespace Nethermind.Mining
{
    public class Ethash : IEthash
    {
        private readonly ConcurrentDictionary<ulong, IEthashDataSet> _cacheCache = new ConcurrentDictionary<ulong, IEthashDataSet>();

        public const int WordBytes = 4; // bytes in word
        public static ulong DatasetBytesInit = (ulong)BigInteger.Pow(2, 30); // bytes in dataset at genesis
        public static ulong DatasetBytesGrowth = (ulong)BigInteger.Pow(2, 23); // dataset growth per epoch
        public static uint CacheBytesInit = (uint)BigInteger.Pow(2, 24); // bytes in cache at genesis
        public static uint CacheBytesGrowth = (uint)BigInteger.Pow(2, 17); // cache growth per epoch
        public const int CacheMultiplier = 1024; // Size of the DAG relative to the cache
        public const ulong EpochLength = 30000; // blocks per epoch
        public const uint MixBytes = 128; // width of mix
        public const int HashBytes = 64; // hash length in bytes
        public const uint DatasetParents = 256; // blockNumber of parents of each dataset element
        public const int CacheRounds = 3; // blockNumber of rounds in cache production
        public const int Accesses = 64; // blockNumber of accesses in hashimoto loop

        public static ulong GetEpoch(BigInteger blockNumber)
        {
            return (ulong)blockNumber / EpochLength;
        }

        public static ulong GetDataSize(BigInteger blockNumber)
        {
            ulong size = DatasetBytesInit + DatasetBytesGrowth * GetEpoch(blockNumber);
            size -= MixBytes;
            while (!IsPrime(size / MixBytes))
            {
                size -= 2 * MixBytes;
            }

            return size;
        }

        public static uint GetCacheSize(BigInteger blockNumber)
        {
            uint size = CacheBytesInit + CacheBytesGrowth * (uint)GetEpoch(blockNumber);
            size -= HashBytes;
            while (!IsPrime(size / HashBytes))
            {
                size -= 2 * HashBytes;
            }

            return size;
        }

        public static bool IsPrime(ulong number)
        {
            if (number == 1)
            {
                return false;
            }

            if (number == 2 || number == 3)
            {
                return true;
            }

            if (number % 2 == 0 || number % 3 == 0)
            {
                return false;
            }

            ulong w = 2;
            ulong i = 5;
            while (i * i <= number)
            {
                if (number % i == 0)
                {
                    return false;
                }

                i += w;
                w = 6 - w;
            }

            return true;
        }

        public static Keccak GetSeedHash(BigInteger blockNumber)
        {
            byte[] seed = new byte[32];
            for (int i = 0; i < blockNumber / EpochLength; i++)
            {
                seed = Keccak.Compute(seed).Bytes; // TODO: optimize
            }

            return new Keccak(seed);
        }

        private readonly BigInteger _2To256 = BigInteger.Pow(2, 256);

        private static readonly Random Random = new Random();

        private static ulong GetRandomNonce()
        {
            byte[] buffer = new byte[8];
            Random.NextBytes(buffer);
            return BitConverter.ToUInt64(buffer, 0);
        }

        private bool IsLessThanTarget(byte[] result, BigInteger target)
        {
            BigInteger resultAsInteger = result.ToUnsignedBigInteger();
            return resultAsInteger < target;
        }

        public (Keccak, ulong) Mine(BlockHeader header, ulong? startNonce = null)
        {
            ulong fullSize = GetDataSize(header.Number);
            ulong nonce = startNonce ?? GetRandomNonce();
            BigInteger target = BigInteger.Divide(_2To256, header.Difficulty);
            Keccak headerHashed = GetTruncatedHash(header);
            
            // parallel for
            byte[] mixHash;
            while(true)
            {
                byte[] result;
                (mixHash, result) = Hashimoto(fullSize, GetOrAddCache(header.Number), headerHashed, null, nonce);
                if (IsLessThanTarget(result, target))
                {
                    break;
                }
                
                unchecked
                {
                    nonce += 1;
                }  
            }

            return (new Keccak(mixHash), nonce);
        }

        internal const uint FnvPrime = 0x01000193;

        internal static void Fnv(uint[] b1, uint[] b2)
        {
            for (uint i = 0; i < b1.Length; i++)
            {
                b1[i] = Fnv(b1[i], b2[i]);
            }
        }

        internal static uint Fnv(uint v1, uint v2)
        {
            return (v1 * FnvPrime) ^ v2;
        }

        internal static uint GetUInt(byte[] bytes, uint offset)
        {
            return BitConverter.ToUInt32(BitConverter.IsLittleEndian ? bytes : Bytes.Reverse(bytes), (int)offset * 4);
        }

        private const int CacheCacheSizeLimit = 6;

        public bool Validate(BlockHeader header)
        {
            IEthashDataSet cache = GetOrAddCache(header.Number);
            ulong fullSize = GetDataSize(header.Number);
            Keccak headerHashed = GetTruncatedHash(header);
            (byte[] _, byte[] result) = Hashimoto(fullSize, cache, headerHashed, header.MixHash, header.Nonce);

            BigInteger threshold = BigInteger.Divide(BigInteger.Pow(2, 256), header.Difficulty);
            return IsLessThanTarget(result, threshold);
        }

        private IEthashDataSet GetOrAddCache(BigInteger blockNumber)
        {
            ulong epoch = GetEpoch(blockNumber);

            ulong? epochToRemove = null;
            IEthashDataSet cache = _cacheCache.GetOrAdd(epoch, e =>
            {
                // TODO: naive, PoC
                if (_cacheCache.Count > CacheCacheSizeLimit)
                {
                    int indextToRemove = Random.Next(CacheCacheSizeLimit);
                    {
                        int index = 0;
                        foreach (ulong epochInCache in _cacheCache.Keys)
                        {
                            if (index == indextToRemove)
                            {
                                epochToRemove = epochInCache;
                            }
                        }
                    }
                }

                uint cacheSize = GetCacheSize(blockNumber);
                Keccak seed = GetSeedHash(blockNumber);
                Console.WriteLine($"Building cache for epoch for block blockNumber {blockNumber}");
                return new EthashCache(cacheSize, seed.Bytes);
            });

            if (epochToRemove.HasValue)
            {
                Console.WriteLine($"Removing cache for epoch {epochToRemove}");
                _cacheCache.TryRemove(epochToRemove.Value, out IEthashDataSet removedItem);
            }

            return cache;
        }

        private static Keccak GetTruncatedHash(BlockHeader header)
        {
            Keccak headerHashed = Keccak.Compute(Rlp.Encode(header, false)); // sic! Keccak here not Keccak512  // this tests fine
            return headerHashed;
        }

        public (byte[], byte[]) Hashimoto(ulong fullSize, IEthashDataSet dataSet, Keccak headerHash, Keccak expectedMixHash, ulong nonce)
        {
            uint hashesInFull = (uint)(fullSize / HashBytes);
            const uint wordsInMix = MixBytes / WordBytes;
            const uint hashesInMix = MixBytes / HashBytes;
            byte[] headerAndNonceHashed = Keccak512.Compute(Bytes.Concat(headerHash.Bytes, nonce.ToByteArray(Bytes.Endianness.Little))).Bytes; // this tests fine
            uint[] mixInts = new uint[MixBytes / WordBytes];

            for (int i = 0; i < hashesInMix; i++)
            {
                Buffer.BlockCopy(headerAndNonceHashed, 0, mixInts, i * headerAndNonceHashed.Length, headerAndNonceHashed.Length);
            }

            uint firstOfheaderAndNonce = GetUInt(headerAndNonceHashed, 0);
            for (uint i = 0; i < Accesses; i++)
            {
                uint p = Fnv(i ^ firstOfheaderAndNonce, mixInts[i % wordsInMix]) % (hashesInFull / hashesInMix) * hashesInMix; // since we take 'hashesInMix' consecutive blocks we want only starting indices of such blocks
                uint[] newData = new uint[wordsInMix];
                for (uint j = 0; j < hashesInMix; j++)
                {
                    uint[] item = dataSet.CalcDataSetItem(p + j);
                    Buffer.BlockCopy(item, 0, newData, (int)(j * item.Length * 4), item.Length * 4);
                }

                Fnv(mixInts, newData);
            }
            
            uint[] cmixInts = new uint[MixBytes / WordBytes / 4];
            for (uint i = 0; i < mixInts.Length; i += 4)
            {
                cmixInts[i / 4] = Fnv(Fnv(Fnv(mixInts[i], mixInts[i+1]), mixInts[i + 2]), mixInts[i + 3]);
            }

            byte[] cmix = new byte[MixBytes / WordBytes];
            Buffer.BlockCopy(cmixInts, 0, cmix, 0, cmix.Length);

            if (expectedMixHash != null && !Bytes.UnsafeCompare(cmix, expectedMixHash.Bytes))
            {
                // TODO: handle properly
                throw new InvalidOperationException(); // TODO: need to change this
            }

            return (cmix, Keccak.Compute(Bytes.Concat(headerAndNonceHashed, cmix)).Bytes); // this tests fine
        }
    }
}