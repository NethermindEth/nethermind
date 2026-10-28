// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Runs one deferred EIP-7805 inclusion check against a block whose execution is <c>VALID</c>.</summary>
/// <remarks>bogota.md § Deferred inclusion checking: the check runs apart from execution, which is answered
/// without waiting for it.</remarks>
public interface IInclusionListComplianceEvaluator
{
    /// <summary>Whether <paramref name="block"/> satisfies <paramref name="inclusionLists"/> under <paramref name="claims"/>,
    /// without re-executing it.</summary>
    /// <param name="block">A block this node executed as <c>VALID</c>, decoded from the request with its access list.</param>
    /// <param name="inclusionLists">One decoded list per committee position.</param>
    /// <param name="claims">The builder's committed omission claims.</param>
    /// <returns><c>null</c> when the state or the execution gas counters needed to judge it are unavailable.</returns>
    Task<bool?> EvaluateAsync(Block block, Transaction[][] inclusionLists, OmissionClaim[] claims, CancellationToken cancellationToken);
}
