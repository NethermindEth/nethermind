// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Evm.Lean.BlockProcessorExtractor.Test;

public partial class BranchAcceptedIterationTests
{
    private const string CompleteBranchClosureDiagnostic = "Unadmitted complete branch syntax/ownership/initializer closure.";
    private const string ProcessOneTupleDiagnostic = "Accepted ProcessOne tuple/predecessor changed.";
    private const string WaitHelper = "private void WaitForCacheClear() => _clearTask.GetAwaiter().GetResult();";
    private const string LocalWaitDeclaration = "static void WaitAndClear(ref Task? task)";
    private const string ProcessOneCall = "blockProcessor.ProcessOne(suggestedBlock, blockOptions, blockTracer, spec, token)";

    [TestCaseSource(nameof(CallableMutations))]
    public void Compile_valid_callable_and_operand_mutations_fail_named_admission(string[] edits, string diagnostic)
    {
        string root = Root();
        Assert.DoesNotThrow(() => BranchAcceptedIterationExtractor.AuditSourceForTest(root),
            "Unmutated source admission must pass before each callable regression.");
        Dictionary<string, string> overrides = new(StringComparer.Ordinal);
        Assert.That(edits.Length % 3, Is.Zero);
        for (int index = 0; index < edits.Length; index += 3)
        {
            string path = edits[index];
            string source = overrides.TryGetValue(path, out string? current) ? current
                : File.ReadAllText(Path.Combine(root, path)).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.That(source, Does.Contain(edits[index + 1]), "Mutation anchor must exist: " + path);
            overrides[path] = source.Replace(edits[index + 1], edits[index + 2], StringComparison.Ordinal);
        }

        Assert.DoesNotThrow(() => BranchAcceptedIterationExtractor.RequireCompilationForTest(root, overrides),
            "A compiler failure is not evidence of source-admission rejection.");
        Assert.That(() => BranchAcceptedIterationExtractor.AuditSourceForTest(root, overrides),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
    }

    private static IEnumerable<TestCaseData> CallableMutations()
    {
        string path = Extractor.BranchPath;
        yield return CallableCase("local_process_one", CallTarget("ProcessOne(suggestedBlock,blockOptions,blockTracer,spec,token)"),
            path, LocalWaitDeclaration,
            "(Block, TxReceipt[]) ProcessOne(Block candidate, ProcessingOptions modes, IBlockTracer tracer, IReleaseSpec rules, CancellationToken cancellation) => (candidate, []);\n        " + LocalWaitDeclaration,
            path, ProcessOneCall, "ProcessOne(suggestedBlock, blockOptions, blockTracer, spec, token)");
        yield return CallableCase("local_commit_tree", Anchor("helper.PreCommitBlock.commitTree"),
            path, "private void PreCommitBlock(BlockHeader block)\n    {",
            "private void PreCommitBlock(BlockHeader block)\n    {\n        void CommitTree(ulong number) { }",
            path, "stateProvider.CommitTree(block.Number);", "CommitTree(block.Number);");
        yield return CallableCase("local_reset", Anchor("reset"),
            path, LocalWaitDeclaration, "void Reset() { }\n        " + LocalWaitDeclaration,
            path, "stateProvider.Reset();", "Reset();");
        yield return CallableCase("local_precommit_shadow", CompleteBranchClosureDiagnostic,
            path, LocalWaitDeclaration, "void PreCommitBlock(BlockHeader header) { }\n        " + LocalWaitDeclaration);

        const string processDelegate = "((Func<Block, ProcessingOptions, IBlockTracer, IReleaseSpec, CancellationToken, (Block, TxReceipt[])>)blockProcessor.ProcessOne)(suggestedBlock, blockOptions, blockTracer, spec, token)";
        yield return CallableCase("delegate_process_one", CallTarget(processDelegate.Replace(" ", "", StringComparison.Ordinal)),
            path, ProcessOneCall, processDelegate);
        yield return CallableCase("delegate_commit_tree", Anchor("helper.PreCommitBlock.commitTree"),
            path, "stateProvider.CommitTree(block.Number);", "Action<ulong> commit = stateProvider.CommitTree;\n        commit(block.Number);");
        yield return CallableCase("lambda_commit_tree", Anchor("helper.PreCommitBlock.commitTree"),
            path, "stateProvider.CommitTree(block.Number);",
            "((Action<ulong>)(number => stateProvider.CommitTree(number)))(block.Number);");
        yield return CallableCase("erase_cache_callback", CompleteBranchClosureDiagnostic,
            path, "private readonly Action<Task> _clearCaches = _ => preWarmer?.ClearCaches();",
            "private readonly Action<Task> _clearCaches = _ => { };");

        yield return CallableCase("conditional_precommit", Anchor("callable.PreCommitBlock"),
            path, "private void PreCommitBlock(BlockHeader block)",
            "[System.Diagnostics.Conditional(\"FORMAL_BRANCH_DISABLED\")]\n    private void PreCommitBlock(BlockHeader block)");
        yield return CallableCase("aliased_conditional_precommit", Anchor("callable.PreCommitBlock"),
            path, "using System;", "using System;\nusing BranchConditional = System.Diagnostics.ConditionalAttribute;",
            path, "private void PreCommitBlock(BlockHeader block)",
            "[BranchConditional(\"FORMAL_BRANCH_DISABLED\")]\n    private void PreCommitBlock(BlockHeader block)");
        yield return CallableCase("inherited_conditional_precommit", Anchor("callable.PreCommitBlock"),
            "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessingCompletedEventArgs.cs", "public class BranchProcessingCompletedEventArgs : EventArgs",
            "public abstract class ConditionalBranchBase\n{\n    [System.Diagnostics.Conditional(\"FORMAL_BRANCH_DISABLED\")]\n    protected virtual void PreCommitBlock(BlockHeader block) { }\n}\n\npublic class BranchProcessingCompletedEventArgs : EventArgs",
            path, ": IBranchProcessor", ": ConditionalBranchBase, IBranchProcessor",
            path, "private void PreCommitBlock(BlockHeader block)", "protected override void PreCommitBlock(BlockHeader block)");
        yield return CallableCase("async_precommit", Anchor("callable.PreCommitBlock"),
            path, "private void PreCommitBlock(BlockHeader block)", "private async void PreCommitBlock(BlockHeader block)");
        yield return CallableCase("async_queue_clear", Anchor("callable.QueueClearCaches"),
            path, "private void QueueClearCaches(Task? preWarmTask)", "private async void QueueClearCaches(Task? preWarmTask)");
        yield return CallableCase("extern_wait", Anchor("callable.WaitForCacheClear"),
            path, WaitHelper,
            "[System.Runtime.InteropServices.DllImport(\"formal-branch-test\")]\n    private static extern void WaitForCacheClear();");
        yield return CallableCase("iterator_wait", Anchor("callable.WaitForCacheClear"),
            path, WaitHelper,
            "private IEnumerable<int> WaitForCacheClear() { yield return 0; _clearTask.GetAwaiter().GetResult(); }");
        yield return CallableCase("partial_wait", Anchor("callable.WaitForCacheClear"),
            path, "public class BranchProcessor(", "public partial class BranchProcessor(",
            path, WaitHelper,
            "private partial void WaitForCacheClear();\n    private partial void WaitForCacheClear() => _clearTask.GetAwaiter().GetResult();");
        yield return CallableCase("erased_partial_cache_callback", Anchor("callable.ErasedClear"),
            path, "public class BranchProcessor(", "public partial class BranchProcessor(",
            path, "private readonly Action<Task> _clearCaches = _ => preWarmer?.ClearCaches();",
            "private readonly Action<Task> _clearCaches = _ => ErasedClear();\n    static partial void ErasedClear();");

        yield return CallableCase("queue_wrong_task_role", Anchor("queueClear"),
            path, "QueueClearCaches(preWarmTask);", "QueueClearCaches(_clearTask);");
        yield return CallableCase("wait_wrong_ref_role", Anchor("waitPrewarm"),
            path, "WaitAndClear(ref preWarmTask);", "WaitAndClear(ref _clearTask);");
        yield return CallableCase("commit_previous_header_role", Anchor("commitTree"),
            path, "PreCommitBlock(suggestedBlock.Header);", "PreCommitBlock(preBlockBaseBlock!);");
        yield return CallableCase("inclusion_duplicates_processed_role", Anchor("inclusion"),
            path, "IsSatisfied(processedBlock, suggestedBlock, stateProvider)", "IsSatisfied(processedBlock, processedBlock, stateProvider)");
        yield return CallableCase("completion_total_instead_of_prefix", Anchor("finally.completion"),
            path, "new BranchProcessingCompletedEventArgs(blocksProcessingEventArgs.Blocks, processedBlocksCount, processingException)",
            "new BranchProcessingCompletedEventArgs(blocksProcessingEventArgs.Blocks, suggestedBlocks.Count, processingException)");

        const string convertedReceiver = "((IBlockProcessor)blockProcessor).ProcessOne(suggestedBlock, blockOptions, blockTracer, spec, token)";
        yield return CallableCase("converted_process_receiver", CallTarget(convertedReceiver.Replace(" ", "", StringComparison.Ordinal)),
            path, ProcessOneCall, convertedReceiver);
        yield return CallableCase("parenthesized_reset_receiver", Anchor("reset"),
            path, "stateProvider.Reset();", "(stateProvider).Reset();");
        yield return CallableCase("parenthesized_commit_receiver", Anchor("helper.PreCommitBlock.commitTree"),
            path, "stateProvider.CommitTree(block.Number);", "(stateProvider).CommitTree(block.Number);");
        yield return CallableCase("narrow_commit_argument", Anchor("helper.PreCommitBlock.commitTree"),
            path, "stateProvider.CommitTree(block.Number);", "stateProvider.CommitTree((ulong)(int)block.Number);");
        yield return CallableCase("checked_commit_argument", Anchor("helper.PreCommitBlock.commitTree"),
            path, "stateProvider.CommitTree(block.Number);", "stateProvider.CommitTree((ulong)checked((int)block.Number));");
        yield return CallableCase("named_process_argument_roles", ProcessOneTupleDiagnostic,
            path, ProcessOneCall,
            "blockProcessor.ProcessOne(suggestedBlock: suggestedBlock, options: blockOptions, blockTracer: blockTracer, spec: spec, token: token)");
        yield return CallableCase("implicit_precommit_conversion", CompleteBranchClosureDiagnostic,
            path, "private void PreCommitBlock(BlockHeader block)",
            "private readonly struct BranchHeaderOperand(BlockHeader header)\n    {\n        public ulong Number => header.Number;\n        public Nethermind.Core.Crypto.Hash256? StateRoot => header.StateRoot;\n        public string ToString(BlockHeader.Format format) => header.ToString(format);\n        public static implicit operator BranchHeaderOperand(BlockHeader header) => new(header);\n    }\n\n    private void PreCommitBlock(BranchHeaderOperand block)");
    }

    private static string Anchor(string name) => BranchAcceptedIterationExtractor.AdmissionDiagnostic(name);
    private static string CallTarget(string expression) => "Branch call target or receiver changed: " + expression;

    private static TestCaseData CallableCase(string name, string diagnostic, params string[] edits) =>
        new TestCaseData(edits, diagnostic).SetName("branch_callable_" + name);
}
