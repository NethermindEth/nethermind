// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

public static partial class StatelessExecutor
{
    private static readonly StatelessValidationResult _defaultFailureResult = new()
    {
        NewPayloadRequestRoot = Hash256.Zero,
        IsSuccess = false,
        ChainId = 0,
        SchemaId = 0
    };

    static partial void InitializeKzg(IReleaseSpec spec)
    {
        if (spec.IsEip4844Enabled && !KzgPolynomialCommitments.IsInitialized)
            KzgPolynomialCommitments.InitializeAsync().GetAwaiter().GetResult();
    }
}
