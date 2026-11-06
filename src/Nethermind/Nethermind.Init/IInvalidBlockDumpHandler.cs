// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Init;

/// <summary>
/// Receives the diagnostic files <see cref="InvalidBlockDiagnostics"/> wrote for a block the processing queue rejected.
/// </summary>
public interface IInvalidBlockDumpHandler
{
    /// <summary>Called once the dump of <paramref name="block"/> has ended with at least one file written.</summary>
    /// <remarks>Runs on the dump thread; no other dump can start until this returns, so long work should be offloaded.</remarks>
    /// <param name="block">The rejected block.</param>
    /// <param name="filePaths">The complete files written for <paramref name="block"/>.</param>
    void OnDumped(Block block, IReadOnlyList<string> filePaths);
}
