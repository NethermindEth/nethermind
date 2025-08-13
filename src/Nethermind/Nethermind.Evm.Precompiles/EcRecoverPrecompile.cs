// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.Precompiles;

public class EcRecoverPrecompile : IPrecompile<EcRecoverPrecompile>
{
    public static readonly EcRecoverPrecompile Instance = new();

    private EcRecoverPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(1);

    public static string Name => "ECREC";

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public long BaseGasCost(IReleaseSpec releaseSpec) => 3000L;

    private readonly byte[] _zero31 = new byte[31];

    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.EcRecoverPrecompile++;
        return inputData.Length >= 128 ? RunInternal(inputData.Span) : RunInternal(inputData);
    }

    private (byte[], bool) RunInternal(ReadOnlyMemory<byte> inputData)
    {
        Span<byte> inputDataSpan = stackalloc byte[128];
        inputData.Span[..Math.Min(128, inputData.Length)]
            .CopyTo(inputDataSpan[..Math.Min(128, inputData.Length)]);

        return RunInternal(inputDataSpan);
    }

    private (byte[], bool) RunInternal(ReadOnlySpan<byte> inputDataSpan)
    {
        ReadOnlySpan<byte> vBytes = inputDataSpan.Slice(32, 32);

        // TEST: CALLCODEEcrecoverV_prefixedf0_d0g0v0
        // TEST: CALLCODEEcrecoverV_prefixedf0_d1g0v0
        if (!Bytes.AreEqual(_zero31, vBytes[..31]))
        {
            return ([], true);
        }

        byte v = vBytes[31];
        if (v != 27 && v != 28)
        {
            return ([], true);
        }

        Span<byte> publicKey = stackalloc byte[65];
        if (!EthereumEcdsa.RecoverAddressRaw(inputDataSpan.Slice(64, 64), Signature.GetRecoveryId(v),
                inputDataSpan[..32], publicKey))
        {
            return ([], true);
        }

        byte[] result = ValueKeccak.Compute(publicKey.Slice(1, 64)).ToByteArray();
        result.AsSpan(0, 12).Clear();
        return (result, true);
    }
}
