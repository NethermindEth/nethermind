// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Stateless;

namespace Nethermind.Eez.Attester;

/// <summary>A streamed window that passed admission: its declared span, the batch calldata and one witnessed block per height.</summary>
/// <param name="Claims">The hash and parent hash the composer sealed for each block.</param>
internal sealed record AdmittedWindow(ulong FromBlock, ulong ToBlock, byte[] PostBatchCalldata, EezStatelessBlock[] Blocks, (Hash256 Hash, Hash256 ParentHash)[] Claims)
    : IDisposable
{
    public void Dispose()
    {
        foreach (EezStatelessBlock block in Blocks)
        {
            block.Witness.Dispose();
        }
    }
}
