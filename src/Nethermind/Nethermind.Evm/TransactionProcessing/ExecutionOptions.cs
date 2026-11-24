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
    /// EIP-8141 mempool admission: run only the validation prefix, halting once the payer is set.
    /// </summary>
    FrameValidationPrefixOnly = 32,

    /// <summary>
    /// Asserts the caller has already verified this transaction's frame signatures against the same spec.
    /// Read only under <see cref="FrameValidationPrefixOnly"/>. Execution with <see cref="SkipValidation"/> independently allows empty signature placeholders.
    /// Some paths compare these options by exact equality, so do not OR it into another mode.
    /// </summary>
    FrameSignaturesPreValidated = 64,

    /// <summary>Frame-gas search: retain fee introspection but defer gas escrow until the final probe.
    /// Only effective together with <see cref="Restore"/>.</summary>
    FrameGasEstimation = 128,

    /// <summary>With <see cref="Warmup"/>: charge gas and value in full, so a sender that cannot pay fails the run.</summary>
    StrictWarmup = 256,

    /// <summary>
    /// Skip potential fail checks and commit state after execution
    /// </summary>
    SkipValidationAndCommit = Commit | SkipValidation,

    /// <summary>
    /// Commit and later restore state also skip validation, use for CallAndRestore
    /// </summary>
    CommitAndRestore = Commit | Restore | SkipValidation
}
