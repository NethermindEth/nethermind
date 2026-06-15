// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Bridges <see cref="ITrieNodeReadObserver"/> onto the <see cref="WitnessCaptureSession"/>: trie
/// node reads observed on the live processing path (e.g. flat's commit-time merkleization) land on
/// the armed session's trie recorder. Inert when no capture is armed.
/// </summary>
public sealed class WitnessTrieNodeReadObserver(WitnessCaptureSession session) : ITrieNodeReadObserver
{
    public bool IsActive => session.TrieRecorder is not null;

    public void OnTrieNodeRead(Hash256 hash, byte[] rlp) => session.TrieRecorder?.Record(hash, rlp);
}
