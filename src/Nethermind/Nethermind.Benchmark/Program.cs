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

using BenchmarkDotNet.Running;
using Nethermind.Benchmarks.Core;
using Nethermind.Benchmarks.Evm;
using Nethermind.Benchmarks.Mining;
using Nethermind.Benchmarks.Rlp;
using Nethermind.Benchmarks.Store;

namespace Nethermind.Benchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            BenchmarkRunner.Run<ByteArrayToHexBenchmarks>();
//            BenchmarkRunner.Run<FromHexBenchmarks>();
//            BenchmarkRunner.Run<BytesCompare>();
//            BenchmarkRunner.Run<BytesIsZero>();
//            BenchmarkRunner.Run<BytesPad>();
//            BenchmarkRunner.Run<Keccak256>();
//            BenchmarkRunner.Run<Keccak512>();
//            
//            BenchmarkRunner.Run<Bn128AddPrecompile>(); // complex, may require experimenting with other libraries
//            BenchmarkRunner.Run<Bn128MulPrecompile>(); // complex, may require experimenting with other libraries
//            BenchmarkRunner.Run<Bn128PairingPrecompile>(); // complex, may require experimenting with other libraries
//            BenchmarkRunner.Run<CalculateJumpDestinations>(); // less important now, since mainly cached
//            BenchmarkRunner.Run<CalculateMemoryCost>();
//            BenchmarkRunner.Run<IntrinsicGasCalculator>(); // less important but might be simple and fun
//            BenchmarkRunner.Run<PrecompileEcRecover>();
//            BenchmarkRunner.Run<PrecompileModExp>();
//            BenchmarkRunner.Run<PrecompileSha2>();
//            BenchmarkRunner.Run<PrecompileRipemd>();
//            BenchmarkRunner.Run<SimpleTransferProcessing>();
//            
//            BenchmarkRunner.Run<EthashHashimoto>();
//            
//            // here we can try some structural changes to RLP design where allocations are limited and performance improved
//            BenchmarkRunner.Run<RlpDecodeAccount>();
//            BenchmarkRunner.Run<RlpEncodeAccount>();
//            BenchmarkRunner.Run<RlpDecodeBlock>();
//            BenchmarkRunner.Run<RlpEncodeBlockBenchmark>();
//            BenchmarkRunner.Run<RlpEncodeHeader>();
//            BenchmarkRunner.Run<RlpEncodeTransaction>();
//              BenchmarkRunner.Run<RlpEncodeLong>();
//              BenchmarkRunner.Run<RlpDecodeLong>();
//              BenchmarkRunner.Run<RlpDecodeInt>();
//              BenchmarkRunner.Run<SignExtend>();
//            BenchmarkRunner.Run<BitwiseOr>();
//            BenchmarkRunner.Run<BitwiseAnd>();
//            BenchmarkRunner.Run<BitwiseNot>();
//            BenchmarkRunner.Run<BitwiseXor>();

//              BenchmarkRunner.Run<PatriciaTree>();
//            
//            BenchmarkRunner.Run<HexPrefixFromBytes>();
//            BenchmarkRunner.Run<PatriciaTree>(); // potentially a bigger rewrite
        }
    }
}