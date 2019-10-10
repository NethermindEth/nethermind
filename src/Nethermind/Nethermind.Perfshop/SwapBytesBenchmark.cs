//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Perfshop
{
    [DisassemblyDiagnoser]
    public class SwapBytesBenchmark
    {
        private Keccak hash = Keccak.OfAnEmptyString;
        private Address addr = Address.SystemUser;

//        [Benchmark(Baseline = true)]
//        public ulong Custom()
//        {
//            // swap adjacent 32-bit blocks
//            result = (number >> 32) | (number << 32);
//            // swap adjacent 16-bit blocks
//            result = ((result & 0xFFFF0000FFFF0000) >> 16) | ((result & 0x0000FFFF0000FFFF) << 16);
//            // swap adjacent 8-bit blocks
//            result = ((result & 0xFF00FF00FF00FF00) >> 8) | ((result & 0x00FF00FF00FF00FF) << 8);
//            return result;
//        }
//
//        [Benchmark]
//        public ulong HostToNetwork()
//        {
//            result = (ulong) IPAddress.HostToNetworkOrder((long) number);
//            return result;
//        }
//        
//        [Benchmark]
//        public ulong ReverseEndiannessUL()
//        {
//            result = BinaryPrimitives.ReverseEndianness(number);
//            return result;
//        }
//        
//        [Benchmark]
//        public ulong ReverseEndiannessL()
//        {
//            result = (ulong)BinaryPrimitives.ReverseEndianness((long)number);
//            return result;
//        }
    }
}