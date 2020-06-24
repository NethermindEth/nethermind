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

using System;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto.Bls;
using G1 = Nethermind.Crypto.ZkSnarks.G1;

namespace Nethermind.Evm.Precompiles.Mcl.Bn256
{
    /// <summary>
    /// https://github.com/matter-labs/eip1962/blob/master/eip196_header.h
    /// </summary>
    public class ShamatarBn256AddPrecompile : IPrecompile
    {
        public static IPrecompile Instance = new ShamatarBn256AddPrecompile();

        public Address Address { get; } = Address.FromNumber(6);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return releaseSpec.IsEip1108Enabled ? 150L : 500L;
        }

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public unsafe (byte[], bool) Run(byte[] inputData)
        {
            Metrics.Bn128AddPrecompile++;
            Span<byte> inputDataSpan = stackalloc byte[128];
            Mcl.PrepareInputData(inputData, inputDataSpan);
            
            Span<byte> output = stackalloc byte[64];
            Span<byte> error = stackalloc byte[256];
            
            int outputLength = 64;
            int errorLength = 256;
            uint externalCallResult;
            
            fixed (byte* inputPtr = &MemoryMarshal.GetReference(inputDataSpan))
            fixed (byte* outputPtr = &MemoryMarshal.GetReference(output))
            fixed (byte* errorPtr = &MemoryMarshal.GetReference(error))
            {
                externalCallResult = RustBls.eip196_perform_operation(
                    1, inputPtr, 128, outputPtr, ref outputLength, errorPtr, ref errorLength);
            }

            (byte[], bool) result;
            if (externalCallResult != 0)
            {
                result = (Bytes.Empty, false);
            }
            else
            {
                result = (output.ToArray(), true);
            }

            return result;
        }
    }
}