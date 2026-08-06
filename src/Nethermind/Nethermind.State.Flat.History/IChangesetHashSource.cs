// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Stands in for the peer-attested checkpoint-hash channel the devp2p wire protocol is expected to add once its
/// BAL-shaped changeset-hash format is settled: today's <see cref="IWindowImportSource"/> carries only raw
/// changeset payloads, with no attested-hash channel a verifier could compare against. A source's claimed value
/// at <paramref name="block"/> is the hash chain <see cref="WindowImportVerifier"/> folds backward from the
/// snap-sync-verified anchor down to and including that block.
/// </summary>
public interface IChangesetHashSource
{
    ValueTask<ValueHash256> GetClaimedChainHashAsync(ulong block, CancellationToken cancellationToken);
}
