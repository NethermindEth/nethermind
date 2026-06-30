// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.StateDiffArchive.Replay;

/// <summary>Thrown when a replayed block's recomputed state root does not match the recorded one.</summary>
public sealed class StateDiffReplayException(ulong blockNumber, Hash256 expected, Hash256 actual)
    : Exception($"State-diff replay produced state root {actual} for block {blockNumber}; expected {expected}. The archive is inconsistent with the chain.")
{
    public ulong BlockNumber => blockNumber;
    public Hash256 Expected => expected;
    public Hash256 Actual => actual;
}
