// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>A lightweight (epoch, root) checkpoint used by fork-choice internals.</summary>
/// <remarks>
/// The SSZ container <see cref="Types.Checkpoint"/> is a nullable-reference class shaped for
/// serialization; fork choice compares and copies checkpoints constantly, so it uses this value
/// type instead. A <c>null</c> root in the SSZ container maps to <see cref="Hash256.Zero"/>.
/// </remarks>
public readonly record struct CheckpointRef(ulong Epoch, Hash256 Root)
{
    public static CheckpointRef From(Types.Checkpoint checkpoint) =>
        new(checkpoint.Epoch, checkpoint.Root ?? Hash256.Zero);

    public Types.Checkpoint ToCheckpoint() => new() { Epoch = Epoch, Root = Root };

    public override string ToString() => $"{Root}@{Epoch}";
}
