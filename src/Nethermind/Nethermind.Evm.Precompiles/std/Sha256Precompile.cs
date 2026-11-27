// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Ssz.Merkleization;

namespace Nethermind.Evm.Precompiles;

public partial class Sha256Precompile : IPrecompile<Sha256Precompile>
{
    public static Address Address { get; } = Address.FromNumber(2);

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        byte[] output = new byte[SHA256.HashSizeInBytes];
        ReadOnlySpan<byte> input = inputData.Span;
        if (ThreadSha256.TryHashDataArm(input, output))
            return output;

        bool success = SHA256.TryHashData(input, output, out int bytesWritten);

        return success && bytesWritten == SHA256.HashSizeInBytes ? output : Errors.Failed;
    }
}
