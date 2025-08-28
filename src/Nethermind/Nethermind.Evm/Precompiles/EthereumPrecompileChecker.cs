// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public class EthereumPrecompileChecker : IPrecompileChecker
{
    public bool IsPrecompile(Address address, IReleaseSpec spec)
    {
        Span<uint> data = MemoryMarshal.Cast<byte, uint>(address.Bytes.AsSpan());
        return (data[4] & 0x0000ffff) == 0
               && data[3] == 0 && data[2] == 0 && data[1] == 0 && data[0] == 0
               && ((data[4] >>> 16) & 0xff) switch
               {
                   0x00 => (data[4] >>> 24) switch
                   {
                       0x01 => true,
                       0x02 => true,
                       0x03 => true,
                       0x04 => true,
                       0x05 => spec.ModExpEnabled,
                       0x06 => spec.Bn128Enabled,
                       0x07 => spec.Bn128Enabled,
                       0x08 => spec.Bn128Enabled,
                       0x09 => spec.BlakeEnabled,
                       0x0a => spec.IsEip4844Enabled,
                       0x0b => spec.Bls381Enabled,
                       0x0c => spec.Bls381Enabled,
                       0x0d => spec.Bls381Enabled,
                       0x0e => spec.Bls381Enabled,
                       0x0f => spec.Bls381Enabled,
                       0x10 => spec.Bls381Enabled,
                       0x11 => spec.Bls381Enabled,
                       _ => false
                   },
                   0x01 => (data[4] >>> 24) switch
                   {
                       0x00 => spec.IsRip7212Enabled,
                       _ => false
                   },
                   _ => false
               };
    }
}
