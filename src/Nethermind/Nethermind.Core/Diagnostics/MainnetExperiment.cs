// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Diagnostics;

/// <summary>
/// Per-block arms of the mainnet newPayload experiment, not for merge. Each block runs one arm, picked by its number,
/// so the arms share one node and one stretch of chain; with rotation off every block runs <see cref="Control"/>,
/// which is master's behaviour.
/// </summary>
public static class MainnetExperiment
{
    /// <summary>Whether blocks rotate through the arms; off on the control image.</summary>
    public static readonly bool RotationEnabled = false;

    public const int Control = 0;
    /// <summary>Cancel the mempool prewarmer's speculative session when newPayload (or getBlobs) arrives, not at enqueue.</summary>
    public const int EarlyCancel = 1;
    /// <summary>Recover senders on at most half the processors, inside the payload's worker budget.</summary>
    public const int RecoveryCap = 2;
    /// <summary>Start the transactions-trie root right after the payload's transactions are decoded, before the engine lock.</summary>
    public const int EarlyTxRoot = 3;
    /// <summary>Look up legacy senders and EIP-7702 authorities in the sender cache the pool fills.</summary>
    public const int ExtendedSenderCache = 4;
    public const int ArmCount = 5;

    private static long s_lastBlock = -1;
    private static int s_currentArm;

    /// <summary>Amsterdam: the block validator recovers a sender only when intrinsic gas cannot be settled without it.</summary>
    public static bool BlockValidatorRecoversOnDemand = RotationEnabled;

    public static int ArmFor(long blockNumber) => RotationEnabled && blockNumber >= 0 ? (int)(blockNumber % ArmCount) : Control;

    public static int CurrentArm => Volatile.Read(ref s_currentArm);

    /// <summary>The arm of the block expected next, for signals that arrive before its newPayload (getBlobs).</summary>
    public static int PredictedArm => ArmFor(Volatile.Read(ref s_lastBlock) + 1);

    public static bool IsActive(int arm) => RotationEnabled && CurrentArm == arm;

    /// <summary>Whether the pool and block paths add legacy senders and authorities to the sender cache.</summary>
    public static bool ExtendedSenderCachePopulated => RotationEnabled;

    /// <summary>Whether recoveries consult the cache for legacy senders and authorities, for the current block's arm.</summary>
    public static bool ExtendedSenderCacheLookups => IsActive(ExtendedSenderCache);

    public static void BeginBlock(long blockNumber)
    {
        if (blockNumber > Volatile.Read(ref s_lastBlock)) Volatile.Write(ref s_lastBlock, blockNumber);
        Volatile.Write(ref s_currentArm, ArmFor(blockNumber));
    }

    /// <summary>Raised when a block is about to arrive or has arrived, for arms that act on it.</summary>
    public static event Action? IncomingBlock;

    public static void SignalIncomingBlock() => IncomingBlock?.Invoke();
}
