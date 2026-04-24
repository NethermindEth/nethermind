// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.TransactionProcessing;

[Flags]
public enum ExecutionOptions
{
    /// <summary>Just accumulate the state</summary>
    None = 0,

    /// <summary>Commit the state after execution</summary>
    Commit = 1,

    /// <summary>Restore state after execution</summary>
    Restore = 2,

    /// <summary>Skip potential fail checks</summary>
    SkipValidation = 4,

    /// <summary>Marker option used by state pre-warmer</summary>
    Warmup = 8,

    /// <summary>
    /// Skip all balance validation regardless of transaction type.
    /// Used by eth_simulateV1 with validation:false to allow zero-balance senders.
    /// </summary>
    SkipBalanceValidation = 16,

    /// <summary>Take a world-state snapshot before execution. Used by BuildUp during block production.</summary>
    TakeSnapshot = 32,

    /// <summary>Skip potential fail checks and commit state after execution</summary>
    SkipValidationAndCommit = Commit | SkipValidation,

    /// <summary>Skip all validations including balance checks, commit state. Used by eth_simulateV1 validate:false.</summary>
    SimulateAndCommit = Commit | SkipValidation | SkipBalanceValidation,

    /// <summary>Commit and later restore state also skip validation, use for CallAndRestore</summary>
    CommitAndRestore = Commit | Restore | SkipValidation,
}
