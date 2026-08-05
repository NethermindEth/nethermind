// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.TransactionProcessing;

[Flags]
public enum ExecutionOptions
{
    /// <summary>
    /// Just accumulate the state
    /// </summary>
    None = 0,

    /// <summary>
    /// Commit the state after execution
    /// </summary>
    Commit = 1,

    /// <summary>
    /// Restore state after execution
    /// </summary>
    Restore = 2,

    /// <summary>
    /// Skip potential fail checks
    /// </summary>
    SkipValidation = 4,

    /// <summary>
    /// Marker option used by state pre-warmer
    /// </summary>
    Warmup = 8,

    /// <summary>
    /// Accumulate state without committing or restoring (block-building mode)
    /// </summary>
    BuildUp = 16,

    /// <summary>
    /// Skip the EIP-3607 sender-has-code check, letting a contract address act as the transaction sender.
    /// </summary>
    /// <remarks>
    /// Used by <c>eth_simulateV1</c> for state-overridden contract senders. Carried as an execution-policy
    /// flag rather than by wrapping the release spec, so the spec's concrete runtime type (and any
    /// chain-specific interfaces such as <c>ITaikoReleaseSpec</c>/<c>IXdcReleaseSpec</c>) survives on the
    /// tx-processor path.
    /// </remarks>
    SkipSenderCodeCheck = 32,

    /// <summary>
    /// Skip potential fail checks and commit state after execution
    /// </summary>
    SkipValidationAndCommit = Commit | SkipValidation,

    /// <summary>
    /// Commit and later restore state also skip validation, use for CallAndRestore
    /// </summary>
    CommitAndRestore = Commit | Restore | SkipValidation
}
