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
    /// EIP-8141 mempool admission: simulate only the validation prefix of a frame transaction,
    /// bounded by <c>MAX_VERIFY_GAS</c> and halting once the payer is set (ethereum/EIPs#12007).
    /// </summary>
    FrameValidationPrefixOnly = 32,

    /// <summary>
    /// Skip potential fail checks and commit state after execution
    /// </summary>
    SkipValidationAndCommit = Commit | SkipValidation,

    /// <summary>
    /// Commit and later restore state also skip validation, use for CallAndRestore
    /// </summary>
    CommitAndRestore = Commit | Restore | SkipValidation
}
