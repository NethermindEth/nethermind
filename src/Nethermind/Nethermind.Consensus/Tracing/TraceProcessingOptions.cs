// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;

namespace Nethermind.Consensus.Tracing;

public static class TraceProcessingOptions
{
    /// <summary>Replays an already processed block without persisting or validating the result.</summary>
    public const ProcessingOptions ReadOnlyReplay =
        ProcessingOptions.ForceProcessing | ProcessingOptions.ReadOnlyChain | ProcessingOptions.LoadNonceFromState | ProcessingOptions.NoValidation;

    /// <summary>
    /// Runs a constructed block of signed transactions without persisting the result or validating the block, keeping each
    /// transaction's signed nonce for the processor to validate.
    /// </summary>
    public const ProcessingOptions ReadOnlySigned =
        ProcessingOptions.ForceProcessing | ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation;
}
