// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History.Proofs;

public interface ICommitmentEmitterSource
{
    CommitmentDepthPolicy Policy { get; }

    CommitmentMetadata Metadata { get; }

    ulong WindowGranularity { get; }

    /// <param name="release">Handed the emitter on dispose, reset, instead of releasing its buffers; see
    /// <see cref="CommitmentEmitter.ForWalk"/>.</param>
    CommitmentEmitter CreateEmitter(Action<CommitmentEmitter>? release = null);
}
