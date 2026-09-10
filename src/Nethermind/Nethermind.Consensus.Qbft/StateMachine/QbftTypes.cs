// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft.StateMachine;

/// <summary>A proposal that reached a prepare quorum in some round, carried into later rounds.</summary>
public sealed record PreparedCertificate(Block Block, IReadOnlyList<SignedData<PreparePayload>> Prepares, int Round, ReadOnlyBlockAccessList? BlockAccessList = null);

/// <summary>Outcome of validating a proposed block against the chain state.</summary>
public readonly record struct BlockValidationResult(bool Success, string? ErrorMessage)
{
    public static BlockValidationResult Valid => new(true, null);
    public static BlockValidationResult Invalid(string error) => new(false, error);
}

/// <summary>Executes a proposed block on top of its parent's state without importing it.</summary>
public interface IQbftBlockValidator
{
    BlockValidationResult ValidateBlock(Block block, ReadOnlyBlockAccessList? blockAccessList);
}

/// <summary>Imports a sealed block into the chain.</summary>
public interface IQbftBlockImporter
{
    bool ImportBlock(Block block, ReadOnlyBlockAccessList? blockAccessList);
}

public sealed record BlockCreationResult(Block Block, ReadOnlyBlockAccessList? BlockAccessList);

/// <summary>Builds the unsealed proposal block for one round.</summary>
public interface IQbftBlockCreator
{
    BlockCreationResult CreateBlock(ulong headerTimestampSeconds, BlockHeader parentHeader);
}

public interface IQbftBlockCreatorFactory
{
    IQbftBlockCreator Create(int roundNumber);
}

/// <summary>Everything a height/round needs that is fixed for the lifetime of the node.</summary>
public interface IQbftFinalState
{
    IReadOnlyList<Address> Validators { get; }
    Address LocalAddress { get; }
    bool IsLocalNodeValidator { get; }
    int Quorum { get; }
    bool IsLocalNodeProposerForRound(ConsensusRoundIdentifier roundIdentifier);
    Address GetProposerForRound(ConsensusRoundIdentifier roundIdentifier);
    IValidatorMulticaster ValidatorMulticaster { get; }
    RoundTimer RoundTimer { get; }
    BlockTimer BlockTimer { get; }
    IQbftBlockCreatorFactory BlockCreatorFactory { get; }
    ITimestamper Clock { get; }
}

/// <summary>Notified when the local node imports a block it took part in agreeing.</summary>
public interface IMinedBlockObserver
{
    void BlockMined(Block block);
}

/// <summary>Schedules the one-shot callbacks that back the block and round timers.</summary>
public interface IBftTimerScheduler
{
    /// <returns>A handle that cancels the callback when disposed.</returns>
    IDisposable Schedule(Action callback, TimeSpan delay);
}

public sealed class ThreadingTimerScheduler : IBftTimerScheduler
{
    public static readonly ThreadingTimerScheduler Instance = new();

    public IDisposable Schedule(Action callback, TimeSpan delay)
    {
        ScheduledCallback scheduled = new(callback);
        scheduled.Start(delay);
        return scheduled;
    }

    private sealed class ScheduledCallback(Action callback) : IDisposable
    {
        private System.Threading.Timer? _timer;
        private int _fired;

        public void Start(TimeSpan delay)
        {
            _timer = new System.Threading.Timer(Fire, null, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
            _timer.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, System.Threading.Timeout.InfiniteTimeSpan);
        }

        private void Fire(object? _)
        {
            if (System.Threading.Interlocked.Exchange(ref _fired, 1) == 0)
            {
                callback();
            }

            _timer?.Dispose();
        }

        public void Dispose()
        {
            System.Threading.Interlocked.Exchange(ref _fired, 1);
            _timer?.Dispose();
        }
    }
}
