// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Sha256Precompile : IPrecompile<Sha256Precompile>
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        Metrics.Sha256Precompile++;

        byte[] output = new byte[SHA256.HashSizeInBytes];

        bool success = SHA256.TryHashData(inputData.Span, output, out int bytesWritten);

        return success && bytesWritten == SHA256.HashSizeInBytes ? output : Errors.Failed;
    }
}
