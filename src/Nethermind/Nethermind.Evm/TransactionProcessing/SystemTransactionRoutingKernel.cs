// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm.TransactionProcessing;

internal static class SystemTransactionRoutingKernel
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool UseSystemProcessor(bool isSystemTransaction, ExecutionOptions options) =>
        isSystemTransaction || options == ExecutionOptions.SkipValidation;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ShouldPayOriginalValue(ExecutionOptions options)
    {
        ExecutionOptions coreOptions = options & ~ExecutionOptions.Warmup;
        return (coreOptions & ExecutionOptions.SkipValidation) != ExecutionOptions.SkipValidation
               && (coreOptions & ExecutionOptions.SkipValidationAndCommit) != ExecutionOptions.SkipValidationAndCommit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ExecutionOptions GetSystemExecutionOptions(ExecutionOptions options, bool payOriginalValue) =>
        payOriginalValue ? options | ExecutionOptions.SkipValidationAndCommit : options;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ParticipatesInNormalBlockCounters(ExecutionOptions options, bool parallel) =>
        (options & ExecutionOptions.SkipValidation) != ExecutionOptions.SkipValidation && !parallel;
}
