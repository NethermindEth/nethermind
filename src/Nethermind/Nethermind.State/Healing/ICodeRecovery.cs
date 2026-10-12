// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Healing;

/// <summary>
/// Fetches contract bytecode that is missing from the local code database from the network.
/// </summary>
public interface ICodeRecovery
{
    /// <summary>
    /// Attempts to fetch the bytecode whose Keccak hash is <paramref name="codeHash"/> from connected peers.
    /// </summary>
    /// <param name="codeHash">Keccak hash of the requested bytecode.</param>
    /// <param name="cancellationToken">Cancels the recovery attempt.</param>
    /// <returns>The recovered bytecode, or <c>null</c> when no peer supplied it in time.</returns>
    /// <remarks>
    /// Implementations must verify that the returned bytes hash to <paramref name="codeHash"/> and must
    /// bound how long they wait — callers block a processing thread on the result.
    /// </remarks>
    Task<byte[]?> Recover(ValueHash256 codeHash, CancellationToken cancellationToken = default);
}
