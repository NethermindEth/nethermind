// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Core.ExecutionRequest;

public static partial class ExecutionRequestExtensions
{
    private static partial byte[] Sha256(ReadOnlySpan<byte> data)
    {
        byte[] output = new byte[SHA256.HashSizeInBytes];

        Accelerators.Sha256(data, output);

        return output;
    }
}
