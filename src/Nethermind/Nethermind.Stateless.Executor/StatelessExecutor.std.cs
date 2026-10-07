// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Stateless.Execution;

public static partial class StatelessExecutor
{
    static partial void InitializeKzg(IReleaseSpec spec)
    {
        if (spec.IsEip4844Enabled && !KzgPolynomialCommitments.IsInitialized)
            KzgPolynomialCommitments.InitializeAsync().GetAwaiter().GetResult();
    }
}
