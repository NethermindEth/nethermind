// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;

namespace Nethermind.Core.ExecutionRequest;

public static partial class ExecutionRequestExtensions
{
    private static partial void Sha256(ReadOnlySpan<byte> data, Span<byte> output) => SHA256.HashData(data, output);
}
