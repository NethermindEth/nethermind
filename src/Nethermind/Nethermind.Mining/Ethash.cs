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
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

[assembly: InternalsVisibleTo("Nethermind.Mining.Test")]

namespace Nethermind.Mining
{
    public class Ethash : IEthash
    {
        private readonly LruCache<uint, IEthashDataSet> _cacheCache = new LruCache<uint, IEthashDataSet>(2);

        public const int WordBytes = 4; // bytes in word
        public static uint DatasetBytesInit = (uint)BigInteger.Pow(2, 30); // bytes in dataset at genesis
        public static uint DatasetBytesGrowth = (uint)BigInteger.Pow(2, 23); // dataset growth per epoch
        public static uint CacheBytesInit = (uint)BigInteger.Pow(2, 24); // bytes in cache at genesis
        public static uint CacheBytesGrowth = (uint)BigInteger.Pow(2, 17); // cache growth per epoch
        public const int CacheMultiplier = 1024; // Size of the DAG relative to the cache
        public const ulong EpochLength = 30000; // blocks per epoch
        public const uint MixBytes = 128; // width of mix
        public const int HashBytes = 64; // hash length in bytes
        public const uint DatasetParents = 256; // blockNumber of parents of each dataset element
        public const int CacheRounds = 3; // blockNumber of rounds in cache production
        public const int Accesses = 64; // blockNumber of accesses in hashimoto loop

        public static uint GetEpoch(BigInteger blockNumber)
        {
            return (uint)(blockNumber / EpochLength);
        }

        public static ulong GetDataSize(uint epoch)
        {
            ulong size = DatasetBytesInit + DatasetBytesGrowth * (ulong)epoch;
            size -= MixBytes;
            while (!IsPrime(size / MixBytes))
            {
                size -= 2 * MixBytes;
            }

            return size;
        }

        public static uint GetCacheSize(uint epoch)
        {
            uint size = CacheBytesInit + CacheBytesGrowth * epoch;
            size -= HashBytes;
            while (!IsPrime(size / HashBytes))
            {
                size -= 2 * HashBytes;
            }

            return size;
        }

        public static bool IsPrime(ulong number)
        {
            if (number == 1U)
            {
                return false;
            }

            if (number == 2U || number == 3U)
            {
                return true;
            }

            if (number % 2U == 0U || number % 3U == 0U)
            {
                return false;
            }

            uint w = 2U;
            uint i = 5U;
            while (i * i <= number)
            {
                if (number % i == 0U)
                {
                    return false;
                }

                i += w;
                w = 6U - w;
            }

            return true;
        }

        public static Keccak GetSeedHash(uint epoch)
        {
            byte[] seed = new byte[32];
            for (uint i = 0; i < epoch; i++)
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

        public (Keccak MixHash, ulong Nonce) Mine(BlockHeader header, ulong? startNonce = null)
        {
            uint epoch = GetEpoch(header.Number);
            ulong fullSize = GetDataSize(epoch);
            ulong nonce = startNonce ?? GetRandomNonce();
            BigInteger target = BigInteger.Divide(_2To256, header.Difficulty);
            Keccak headerHashed = GetTruncatedHash(header);
            
            // parallel for (just with ulong...) - adjust based on the available mining threads, low priority
            byte[] mixHash;
            while(true)
            {
                byte[] result;
                (mixHash, result) = Hashimoto(fullSize, GetOrAddCache(epoch), headerHashed, null, nonce);
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

        public bool Validate(BlockHeader header)
        {
            uint epoch = GetEpoch(header.Number);
            IEthashDataSet cache = GetOrAddCache(epoch);
            ulong fullSize = GetDataSize(epoch);
            Keccak headerHashed = GetTruncatedHash(header);
            (byte[] _, byte[] result) = Hashimoto(fullSize, cache, headerHashed, header.MixHash, header.Nonce);

            BigInteger threshold = BigInteger.Divide(BigInteger.Pow(2, 256), header.Difficulty);
            return IsLessThanTarget(result, threshold);
        }

        public void PrecomputeCache(uint epoch)
        {
            GetOrAddCache(epoch);
        }
        
        private IEthashDataSet GetOrAddCache(uint epoch)
        {
            IEthashDataSet dataSet = _cacheCache.Get(epoch);
            if (dataSet == null)
            {
                uint cacheSize = GetCacheSize(epoch);
                Keccak seed = GetSeedHash(epoch);
                Console.WriteLine($"Building cache for epoch {epoch}");
                dataSet = new EthashCache(cacheSize, seed.Bytes);
                _cacheCache.Set(epoch, dataSet);
            }
           
            return dataSet;
        }

        private static Keccak GetTruncatedHash(BlockHeader header)
        {
            Keccak headerHashed = Keccak.Compute(Rlp.Encode(header, RlpBehaviors.ExcludeBlockMixHashAndNonce)); // sic! Keccak here not Keccak512
            return headerHashed;
        }

        public (byte[], byte[]) Hashimoto(ulong fullSize, IEthashDataSet dataSet, Keccak headerHash, Keccak expectedMixHash, ulong nonce)
        {
            uint hashesInFull = (uint)(fullSize / HashBytes); // TODO: at current rate would cover around 200 years... but will the block rate change? what with private chains with shorter block times?
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