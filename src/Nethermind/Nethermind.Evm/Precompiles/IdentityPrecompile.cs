// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public sealed class IdentityPrecompile : IPrecompile<IdentityPrecompile>
{
    public static IdentityPrecompile Instance { get; } = new();

    private IdentityPrecompile()
    {
    }

    // Byte literals rather than hex parsing: lets ILC precompute this type, so the zkEVM guest runs no class constructor.
    public static Address Address { get; } = new((ReadOnlySpan<byte>)[0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04]);

    public string Name => "ID";

    // Caching disabled: the copy operation is O(n) and the cache key hash is also O(n),
    // making caching strictly worse than direct execution for this precompile.
    public bool SupportsCaching => false;

    public ulong BaseGasCost(IReleaseSpec releaseSpec) => 15UL;

    public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        3UL * EvmCalculations.Div32Ceiling(inputData.Length);

    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => inputData.ToArray();
}
