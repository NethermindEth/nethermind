using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Mining
{
    public class Ethash
    {
        public const int WordBytes = 4; // bytes in word
        public ulong DatasetBytesInit = (ulong)BigInteger.Pow(2, 30); // bytes in dataset at genesis
        public ulong DatasetBytesGrowth = (ulong)BigInteger.Pow(2, 23); // dataset growth per epoch
        public ulong CacheBytesInit = (ulong)BigInteger.Pow(2, 24); // bytes in cache at genesis
        public ulong CacheBytesGrowth = (ulong)BigInteger.Pow(2, 17); // cache growth per epoch
        public const int CacheMultiplier = 1024; // Size of the DAG relative to the cache
        public const ulong EpochLength = 30000; // blocks per epoch
        public const int MixBytes = 128; // width of mix
        public const int HashBytes = 64; // hash length in bytes
        public const int DatasetParents = 256; // number of parents of each dataset element
        public const int CacheRounds = 3; // number of rounds in cache production
        public const int Accesses = 64; // number of accesses in hashimoto loop

        public ulong GetDataSize(BigInteger blockNumber)
        {
            ulong size = DatasetBytesInit + DatasetBytesGrowth * ((ulong)blockNumber / EpochLength);
            size -= MixBytes;
            while (!IsPrime(size / MixBytes))
            {
                size -= 2 * MixBytes;
            }

            return size;
        }

        public ulong GetCacheSize(BigInteger blockNumber)
        {
            ulong size = CacheBytesInit + CacheBytesGrowth * ((ulong)blockNumber / EpochLength);
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

        public Keccak GetSeedHash(BigInteger blockNumber)
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

        private bool IsGreaterThanTarget(byte[] result, byte[] target)
        {
            throw new NotImplementedException();
        }

        public ulong Mine(long fullSize, byte[] dataSet, BlockHeader header, BigInteger difficulty)
        {
            ulong nonce = GetRandomNonce();
            byte[] target = BigInteger.Divide(_2To256, difficulty).ToBigEndianByteArray();
            while (IsGreaterThanTarget(HashimotoFull(), target))
            {
                unchecked
                {
                    nonce += 1;
                }
            }

            return nonce;
        }

        public byte[][] CalDataSet(ulong setSize, byte[][] cache)
        {
            byte[][] dataSet = new byte[(uint)(setSize / HashBytes)][];
            for (uint i = 0; i < dataSet.Length; i++)
            {
                dataSet[i] = CalcDataSetItem(i, cache);
            }

            return dataSet;
        }

        // TODO: optimize, check, work in progress
        // this data set will be in GBs
        public byte[] CalcDataSetItem(uint index, byte[][] cache)
        {
            ulong r = HashBytes / WordBytes;
            ulong cacheSize = (ulong)cache.Length;

            byte[] mix = (byte[])cache[index % cacheSize].Clone();
            SetUInt(mix, 0, index ^ GetUInt(mix, 0));
            mix = Keccak512.Compute(mix).Bytes;

            for (ulong i = 0; i < DatasetParents; i++)
            {
                ulong cacheIndex = Fnv(index ^ i, mix[i ^ r]);
                mix = Fnv(mix, cache[cacheIndex % cacheSize]); // TODO: check
            }

            return Keccak512.Compute(mix).Bytes;
        }

        // TODO: optimize, check, work in progress
        public byte[][] BuildCache(ulong cacheSize, byte[] seed)
        {
            Debug.Assert(seed.Length == HashBytes, $"Expected {nameof(seed)} to be {HashBytes} bytes long");
            byte[][] cache = new byte[cacheSize / HashBytes][];
            cache[0] = seed;
            for (ulong i = 1; i < cacheSize; i++)
            {
                cache[i] = Keccak512.Compute(cache[i - 1]).Bytes;
            }

            for (int i = 0; i < CacheRounds; i++)
            {
                for (int j = 0; j < cache.Length; j++)
                {
                    int v = cache[j][0] % cache.Length;
                    cache[j] = Keccak512.Compute(cache[i - 1 + cache.Length].Xor(cache[v])).Bytes;
                }
            }

            return cache;
        }

        public const uint FnvPrime = 0x01000193;

        // TODO: optimize, check, work in progress
        private static byte[] Fnv(byte[] b1, byte[] b2)
        {
            Debug.Assert(b1.Length == b2.Length, "FNV expecting same length arrays");
            Debug.Assert(b1.Length % 4 == 0, "FNV expecting length to be a multiple of 4");

            byte[] result = new byte[b1.Length];
            for (int i = 0; i < b1.Length / 4; i++)
            {
                uint v1 = GetUInt(b1, i);
                uint v2 = GetUInt(b2, i);
                SetUInt(result, i, Fnv(v1, v2));
            }

            return result;
        }

        private static void SetUInt(byte[] bytes, int offset, uint value)
        {
            byte[] valueBytes = value.ToByteArray(Bytes.Endianness.Little);
            Buffer.BlockCopy(valueBytes, 0, bytes, offset, 4);
        }

        private static uint GetUInt(byte[] bytes, int offset)
        {
            return bytes.Slice(offset, 4).ToUInt32(Bytes.Endianness.Little);
        }

        public static ulong Fnv(ulong v1, ulong v2)
        {
            return (v1 * FnvPrime) ^ v2;
        }

        public static uint Fnv(uint v1, uint v2)
        {
            return (v1 * FnvPrime) ^ v2;
        }

        public byte[] Hashimoto()
        {
            throw new NotImplementedException();
        }

        public byte[] HashimotoLight()
        {
            throw new NotImplementedException();
        }

        public byte[] HashimotoFull()
        {
            throw new NotImplementedException();
        }
    }
}