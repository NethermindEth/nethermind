// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Sha256Precompile : IPrecompile<Sha256Precompile>
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        byte[] output = new byte[32];

        ZiskBindings.Crypto.sha256_c(inputData.Span, (nuint)inputData.Length, output);

        return output;
    }
}
