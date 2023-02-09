// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public static class AddressExtensions
    {
        private static byte[] _nineteenZeros = new byte[19];

        public static bool IsPrecompile(this Address address, IReleaseSpec releaseSpec)
        {
            if (!Bytes.AreEqual(address.Bytes.AsSpan(0, 19), _nineteenZeros))
            {
                return false;
            }

            int precompileCode = address[19];
            return precompileCode switch
            {
                1 => true,
                2 => true,
                3 => true,
                4 => true,
                5 => releaseSpec.ModExpEnabled,
                6 => releaseSpec.Bn128Enabled,
                7 => releaseSpec.Bn128Enabled,
                8 => releaseSpec.Bn128Enabled,
                9 => releaseSpec.BlakeEnabled,
                10 => releaseSpec.Bls381Enabled,
                11 => releaseSpec.Bls381Enabled,
                12 => releaseSpec.Bls381Enabled,
                13 => releaseSpec.Bls381Enabled,
                14 => releaseSpec.Bls381Enabled,
                15 => releaseSpec.Bls381Enabled,
                16 => releaseSpec.Bls381Enabled,
                17 => releaseSpec.Bls381Enabled,
                18 => releaseSpec.Bls381Enabled,
                20 => releaseSpec.IsEip4844Enabled,
                _ => false
            };
        }
    }
}
