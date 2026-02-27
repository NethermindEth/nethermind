// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles;

public class Ripemd160Precompile : IPrecompile<Ripemd160Precompile>
{
    public static readonly Ripemd160Precompile Instance = new();

    // missing in .NET Core
    //        private static RIPEMD160 _ripemd;

    private Ripemd160Precompile()
    {
        // missing in .NET Core
        //            _ripemd = RIPEMD160.Create();
        //            _ripemd.Initialize();
    }

    public static Address Address { get; } = Address.FromNumber(3);

    public static string Name => "RIPEMD160";
    // The fast path cannot be used for RIPEMD-160 because the EIP-161 Parity touch bug
    // workaround (applied in RunPrecompile in VirtualMachine.cs) requires touching the
    // precompile address *before* execution. The fast path touches *after* success, which
    // would break consensus for historical sync.
    public bool SupportsFastPath => false;

    public long BaseGasCost(IReleaseSpec releaseSpec) => 600L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        120L * EvmCalculations.Div32Ceiling((ulong)inputData.Length);

    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Ripemd160Precompile++;
        return Ripemd.Compute(inputData.Span);
    }
}
