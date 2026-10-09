// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class KzgPointEvaluationPrecompile
{
    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _) => RunInternal(inputData);

    private static partial string DescribeFailedVerification(ReadOnlySpan<byte> z, ReadOnlySpan<byte> y, ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> proof) =>
        Errors.Failed;
}
