// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

// Computed on access, so the guest runs no class constructor for this type.
public static partial class StatelessExecutor
{
    private static StatelessValidationResult _defaultFailureResult => new()
    {
        NewPayloadRequestRoot = Hash256.Zero,
        IsSuccess = false,
        ChainId = 0,
        SchemaId = 0
    };
}
