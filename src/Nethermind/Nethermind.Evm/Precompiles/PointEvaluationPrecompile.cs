//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles
{
    public class PointEvaluationPrecompile : IPrecompile
    {
        public static readonly IPrecompile Instance = new PointEvaluationPrecompile();

        private static readonly ReadOnlyMemory<byte> PointEvaluationSuccessfulResponse =
                                                        BitConverter.GetBytes((long)KzgPolynomialCommitments.FieldElementsPerBlob)
                                                .Concat(KzgPolynomialCommitments.BlsModulus.ToLittleEndian())
                                                .ToArray();

        static PointEvaluationPrecompile() => KzgPolynomialCommitments.Inititalize();

        public Address Address { get; } = Address.FromNumber(0x14);

        public long BaseGasCost(IReleaseSpec releaseSpec) => 50000L;

        public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0;

        public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            Metrics.PointEvaluationPrecompile++;
            if (inputData.Length != 192)
            {
                return (ReadOnlyMemory<byte>.Empty, false);
            }

            ReadOnlySpan<byte> versionedHash = inputData.Span[..32];
            ReadOnlySpan<byte> z = inputData.Span[32..64];
            ReadOnlySpan<byte> y = inputData.Span[64..96];
            ReadOnlySpan<byte> commitment = inputData.Span[96..144];
            if (!KzgPolynomialCommitments.CommitmentToHashV1(commitment).SequenceEqual(versionedHash))
            {
                return (ReadOnlyMemory<byte>.Empty, false);
            }

            ReadOnlySpan<byte> proof = inputData.Span[144..192];
            if (!KzgPolynomialCommitments.VerifyProof(commitment, z, y, proof))
            {
                return (ReadOnlyMemory<byte>.Empty, false);
            }
            return (PointEvaluationSuccessfulResponse, true);
        }
    }
}
