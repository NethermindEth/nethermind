// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Evm.Precompiles;

public partial class Sha256Precompile : IPrecompile<Sha256Precompile>
{
    // The 32-byte output is copied into the caller's return-data buffer before
    // the next precompile call runs, so a single reused buffer is safe in the
    // single-threaded guest and avoids a heap allocation per call.
    private static readonly byte[] _output = new byte[32];

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        Accelerators.Sha256(inputData.Span, _output);

        return _output;
    }
}
