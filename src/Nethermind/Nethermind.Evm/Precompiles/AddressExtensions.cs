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
                0x01 => true,
                0x02 => true,
                0x03 => true,
                0x04 => true,
                0x05 => releaseSpec.ModExpEnabled,
                0x06 => releaseSpec.Bn128Enabled,
                0x07 => releaseSpec.Bn128Enabled,
                0x08 => releaseSpec.Bn128Enabled,
                0x09 => releaseSpec.BlakeEnabled,
                0x0a => releaseSpec.IsEip4844Enabled,
                0x0c => releaseSpec.Bls381Enabled,
                0x0d => releaseSpec.Bls381Enabled,
                0x0e => releaseSpec.Bls381Enabled,
                0x0f => releaseSpec.Bls381Enabled,
                0x10 => releaseSpec.Bls381Enabled,
                0x11 => releaseSpec.Bls381Enabled,
                0x12 => releaseSpec.Bls381Enabled,
                0x13 => releaseSpec.Bls381Enabled,
                0x14 => releaseSpec.Bls381Enabled,
                _ => false
            };
        }
    }
}
