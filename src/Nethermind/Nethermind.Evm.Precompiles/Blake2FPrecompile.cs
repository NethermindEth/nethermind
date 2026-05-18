// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-152" />
/// </summary>
public partial class Blake2FPrecompile : IPrecompile<Blake2FPrecompile>
{
    private const int RequiredInputLength = 213;
    private static readonly ReadOnlyMemory<byte> InvalidLengthInput = ReadOnlyMemory<byte>.Empty;
    private static readonly ReadOnlyMemory<byte> InvalidFlagInput = new byte[RequiredInputLength].WithValueAt(212, 0xFF);

    public static Blake2FPrecompile Instance { get; } = new();

    private Blake2FPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(9);

    public static string Name => "BLAKE2F";

    public long BaseGasCost(IReleaseSpec _) => 0;

    public ReadOnlyMemory<byte> NormalizeInput(ReadOnlyMemory<byte> inputData)
    {
        if (inputData.Length != RequiredInputLength) return InvalidLengthInput;
        if (inputData.Span[212] is not 0 and not 1) return InvalidFlagInput;
        return inputData;
    }

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        if (inputData.Length != RequiredInputLength)
            return 0;

        byte finalBlock = inputData.Span[212];

        if (finalBlock != 0 && finalBlock != 1)
            return 0;

        uint rounds = BinaryPrimitives.ReadUInt32BigEndian(inputData[..4].Span);

        return rounds;
    }

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryPrepareInput(
        ReadOnlyMemory<byte> inputData,
        out ReadOnlySpan<byte> inputSpan,
        out Result<byte[]> result)
    {
        inputSpan = inputData.Span;

        if (inputData.Length != RequiredInputLength)
        {
            result = Errors.InvalidInputLength;
            return false;
        }

        byte finalBlock = inputSpan[212];

        if (finalBlock != 0 && finalBlock != 1)
        {
            result = Errors.InvalidFinalBlockFlag;
            return false;
        }

        result = default;
        return true;
    }
}
