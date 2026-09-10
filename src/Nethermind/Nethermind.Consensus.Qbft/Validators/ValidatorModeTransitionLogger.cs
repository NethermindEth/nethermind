// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Qbft.Config;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.Validators;

/// <summary>Logs when the validator selection mode changes between a block and its child.</summary>
public sealed class ValidatorModeTransitionLogger(BftForksSchedule forksSchedule, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<ValidatorModeTransitionLogger>();

    public void LogTransitionChange(BlockHeader parentHeader)
    {
        BftConfigSnapshot current = forksSchedule.GetFork((long)parentHeader.Number, parentHeader.Timestamp);
        BftConfigSnapshot next = forksSchedule.GetFork((long)parentHeader.Number + 1, parentHeader.Timestamp);
        if (current.ValidatorContractAddress != next.ValidatorContractAddress && _logger.IsInfo)
        {
            _logger.Info($"Transitioning validator selection mode from {Describe(current)} to {Describe(next)}");
        }
    }

    public static string Describe(BftConfigSnapshot config) =>
        config.ValidatorContractAddress is null ? "blockheader" : $"contract (address: {config.ValidatorContractAddress})";
}
