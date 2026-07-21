// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.Precompiles;

public partial class Sha256Precompile : IPrecompile<Sha256Precompile>
{
    // Reused buffer: the zkVM guest is single-threaded and copies the result out before the next call.
    private static readonly byte[] _output = new byte[SHA256.HashSizeInBytes];

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        Accelerators.Sha256(inputData.Span, _output);

        return _output;
    }
}
