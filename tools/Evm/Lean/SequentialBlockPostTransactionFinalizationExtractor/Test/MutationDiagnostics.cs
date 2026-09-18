// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor.Test;

public sealed partial class SequentialBlockPostTransactionFinalizationExtractorTests
{
    private static string ExpectedSourceDiagnostic(string mutation) => mutation switch
    {
        "delete-commit" => "ProcessBlock must retain exactly three CommitState(spec) calls.",
        "duplicate-commit" => "ProcessBlock must retain exactly three CommitState(spec) calls.",
        "reorder-commit" => "ProcessBlock must retain exactly three CommitState(spec) calls.",
        "commit-root-flags" => "CommitState must contain exactly one typed direct _stateProvider.Commit with commitRoots:false.",
        "commit-roots-disabled" => "CommitStateAndStorageRoots must contain exactly one typed direct _stateProvider.Commit with commitRoots:true.",
        "invert-eip4844" => "EIP-4844 guard is missing or ambiguous.",
        "swap-blob-arguments" => "blob gas calculation",
        "invert-background" => "background receipt guard is missing or ambiguous.",
        "invert-main-thread" => "main-thread guard is missing or ambiguous.",
        "invert-state-root" => "state-root guard is missing or ambiguous.",
        "wrong-receipt-root-input" => "The synchronous CalculateReceiptsRoot(receipts,spec,block) arm is missing.",
        "swap-end-trace-argument" => "EndBlockTrace must retain the source null-task argument.",
        "task-reassignment" => "The receipt task reaching definitions changed",
        "bal-after-hash" => "BAL finalization",
        "early-return" => "receipts return",
        "using-alias" => "introduced an unsupported alias binding.",
        "ref-alias" => "The receipt task is used through a ref alias.",
        "ref-out" => "The receipt task is used through a ref alias.",
        "task-capture" => "The receipt task is captured by a lambda or local function.",
        "local-function-capture" => "nested local function",
        "indirect-task-use" => "The receipt task has an unwhitelisted local use",
        "task-helper-assignment" => "un-audited delegate creation",
        "callback-conditional" => "TransactionsExecuted signal has an unexpected conditional guard",
        "callback-lambda" => "TransactionsExecuted signal expected one",
        "callback-duplicate" => "TransactionsExecuted signal expected one",
        "callback-try-finally" => "TransactionsExecuted signal must be the exact outer ProcessBlock expression statement",
        "opaque-bound-helper" => "bound helper 'SetAccountChanges' through unwhitelisted",
        "opaque-helper-write" => "Pinned five-tree typed effect ledger drift",
        "opaque-local-function" => "nested local function",
        "opaque-task-run" => "un-audited delegate creation",
        "opaque-callback" => "un-audited delegate invocation",
        "receipts-ignored-result" => "The standard executor result must initialize the exact receipts local.",
        "receipts-reassigned-result" => "The receipts local reference count changed;",
        "receipts-capture" => "The receipts local reference count changed;",
        "receipts-local-reassignment" => "The receipts local reference count changed;",
        "competing-blob-write" => "The EIP-4844 BlobGasUsed assignment is missing or redirected.",
        "competing-root-write" => "The synchronous ReceiptsRoot assignment is missing or redirected.",
        "competing-background-root-write" => "The synchronous ReceiptsRoot assignment is missing or redirected.",
        "duplicate-roots-post-finally" => "extra roots commit after finally is rejected",
        "direct-commit" => "direct typed _stateProvider.Commit invocation",
        "direct-commit-post-finally" => "direct typed _stateProvider.Commit invocation",
        "helper-equivalent-commit" => "unwhitelisted source-local helper 'CommitStateEquivalent'",
        "flush-state" => "unwhitelisted source-local helper 'FlushState'",
        "rewrite-header" => "unwhitelisted source-local helper 'RewriteHeader'",
        "nested-flush-state" => "NestedCommitHelper",
        "nested-rewrite-header" => "NestedHeaderHelper",
        "delegate-header-write" => "un-audited delegate invocation 'RewriteAfterHash(header)'",
        "property-accessor-header-write" => "source-local property 'RewriteAfterHash' with an executable accessor",
        "constructor-header-write" => "source-owned constructor/object creation",
        "interface-dispatch-header-write" => "unbound source interface/virtual dispatch '_headerMutator.Rewrite(header)'",
        "operator-header-write" => "source-owned binary operator",
        "conversion-header-write" => "source-owned user-defined conversion",
        "dynamic-member-sentinel" => "un-audited dynamic member invocation",
        "lazy-initializer-header-write" => "initializer ledger rejects an un-audited pinned initializer lambda",
        "external-interface-getter-header-write" => "property/indexer/event activation ledger drift",
        "event-accessor-header-write" => "property/indexer/event activation ledger drift",
        "idisposable-disposal-header-write" => "typed effect ledger drift",
        "metrics-timer-sink-header-write" => "typed effect ledger drift",
        "reflection-reentry-header-write" => "reflection/reentry API",
        "duplicate-account-false-arm" => "SetAccountChanges(block) must have exactly one true-arm source call.",
        "duplicate-state-root-false-arm" => "ComputeStateRoot(header) must have exactly one true-arm source call.",
        "deconstruction-state-root-write" => "The synchronous ReceiptsRoot assignment is missing or redirected.",
        "direct-state-root-write" or "direct-account-changes-write" or "deconstruction-account-changes-write" or
            "compound-state-root-write" or "compound-account-changes-write" or "local-state-root-write" or
            "local-account-changes-write" or "duplicate-state-root-helper-write" or "duplicate-account-helper-write" or
            "redirect-state-root-helper-value" or "redirect-account-helper-value" or "compound-state-root-helper-write" or
            "compound-account-helper-write" => "Pinned five-tree typed effect ledger drift",
        "later-header-overwrite" => "The synchronous ReceiptsRoot assignment is missing or redirected.",
        "later-hash-overwrite" => "The header hash assignment is missing or duplicated.",
        "background-result-predicate" => "background-result guard is missing or ambiguous.",
        "finally-predicate" => "finally task-observation guard is missing or ambiguous.",
        "wrap-post-transaction-commit" => "admitted tail action 'block.post-transaction-commit' has an unexpected conditional guard",
        "wrap-task-initializer" => "The receipt background task must be initialized to null.",
        "wrap-rewards" => "admitted tail action 'block.rewards' has an unexpected conditional guard",
        "wrap-withdrawals" => "admitted tail action 'block.withdrawals' has an unexpected conditional guard",
        "wrap-finalization-commit" => "admitted tail action 'block.finalization-commit' has an unexpected conditional guard",
        "wrap-execution-requests" => "admitted tail action 'block.execution-requests' has an unexpected conditional guard",
        "wrap-end-trace" => "admitted tail action 'block.end-block-trace' has an unexpected conditional guard",
        "wrap-storage-roots" => "admitted tail action 'block.storage-roots-commit' has an unexpected conditional guard",
        "wrap-bal" => "admitted tail action 'block.bal-finalization' has an unexpected conditional guard",
        "wrap-hash" => "admitted tail action 'block.hash' has an unexpected conditional guard",
        "wrap-return" => "admitted tail action 'block.return-receipts' has an unexpected conditional guard",
        _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
    };

    private static string ExpectedPublicationDiagnostic(string mutation) => mutation switch
    {
        "disposal-wrong-target" => "processOne.disposal-finally' target changed",
        "encoded-bal-overwrite" or "encoded-bal-reverse" or "copy-account" or "copy-requests" or "copy-bal" or
            "post-validation-extra-statement" => "PostValidation four-field publication order or fallback changed.",
        "duplicate-store" => "StoreTxReceipts must be the exact InsertDeferred call.",
        "validator-bal-side-effect" => "Validator GeneratedBlockAccessList observation must have exactly one source write.",
        "validator-bal-hidden" => "Validator proposed GeneratedBAL write is not in the exact mismatch-to-rejection arm.",
        "no-validation-inverted" or "rejection-dispose-missing" or "validator-arguments" or "acceptance-dispose" =>
            "Validation must retain the exact short-circuit guard, rejection cleanup/throw, and PostValidation suffix.",
        "return-suggested" or "return-different-receipts" or "store-guard-inverted" or "validation-conditional" or
            "processed-flag" or "commit-tree" or "reset" or "retry-filter" =>
            "ProcessOne normal-result suffix, guards, disposal, or exact return changed.",
        _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
    };
}
