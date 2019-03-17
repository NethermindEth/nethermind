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
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Network.Crypto;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nethermind.Network.Benchmarks
{
    [MemoryDiagnoser]
    [CoreJob(true)]
    public class KdfDerivation
    {
        private static byte[] z = Bytes.FromHexString("22ca1111ca383ef9d090ca567245eb72f80d8730fd4e1507e9a23bcdb3bb5a87");

        private MeadowKdf _improved = new MeadowKdf();
        private OptimizedKdf _improved2 = new OptimizedKdf();

        [GlobalSetup]
        public void Setup()
        {
            Check(Old(), Alternative());
            Check(Old(), Current());
        }

        private void Check(byte[] a, byte[] b)
        {
            if (!a.SequenceEqual(b))
            {
                Console.WriteLine($"Outputs are different {a.ToHexString()} != {b.ToHexString()}!");
                throw new InvalidOperationException();
            }

            Console.WriteLine($"Outputs are the same: {a.ToHexString()}");
        }

        [Benchmark]
        public byte[] Alternative()
        {
            var result = _improved.DeriveKeyKDF(z, 32);
            return result;
        }

        [Benchmark]
        public byte[] Current()
        {
            var result = _improved2.Derive(z);
            return result;
        }

        [Benchmark]
        public byte[] Old()
        {
            ConcatKdfBytesGenerator current = new ConcatKdfBytesGenerator(new Sha256Digest());
            IDerivationParameters kdfParam = new KdfParameters(z, new byte[0]);
            current.Init(kdfParam);
            var result = new byte[32];
            current.GenerateBytes(result, 0, 32);
            return result;
        }
    }
}