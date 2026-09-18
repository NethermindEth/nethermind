// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Nethermind.Evm.Lean.OrdinaryPostNonceDispatchExtractor;

/// <summary>Emits the theorem-free, source-derived post-nonce dispatch transition.</summary>
/// <remarks>
/// The generated module contains only the bounded executable transition. Its source bindings are
/// emitted as inspectable data; reference semantics and refinement obligations live in separate
/// handwritten modules so the generated file cannot hide a proof or a semantic oracle.
/// </remarks>
internal static class LeanEmitter
{
    internal static byte[] Emit(IrDocument document, string irSha256)
    {
        ValidateSourceLoweredShape(document);
        LoweredTerms terms = LowerSourceTerms(document);
        string source = LeanSource
            .Replace("__IR_SHA256__", irSha256, StringComparison.Ordinal)
            .Replace("__STAGES__", Quoted(document.Dispatch.Stages.Select(static stage => stage.Id)), StringComparison.Ordinal)
            .Replace("__BRANCHES__", Quoted(document.Dispatch.Branches.Select(static branch => branch.Id + ":" + branch.TerminalKind + ":" + branch.Condition + ":" + SourceExpressionIdentity(branch.ConditionAst))), StringComparison.Ordinal)
            .Replace("__EFFECTS__", Quoted(document.Dispatch.Effects.Select(static effect => effect.Id + ":" + effect.Effect)), StringComparison.Ordinal)
            .Replace("__PRELOAD_STATES__", Quoted(document.Dispatch.PreloadStates.Select(static state => state.ToString())), StringComparison.Ordinal)
            .Replace("__OPERATIONS__", Quoted(document.Dispatch.Semantics.Operations.Select(static operation => operation.Id + ":" + operation.Formula + ":" + SourceExpressionIdentity(operation.ExpressionAst))), StringComparison.Ordinal)
            .Replace("__BINDINGS__", Quoted(document.Dispatch.MethodBindings.Concat(document.Dispatch.RouteBindings).Select(BindingIdentity)), StringComparison.Ordinal)
            .Replace("__ADAPTERS__", Quoted(document.Dispatch.Semantics.AdapterPremises.Select(static premise => premise.Id + ":" + premise.DomainPredicate + ":" + SourceExpressionIdentity(premise.DomainAst))), StringComparison.Ordinal)
            .Replace("__OPTION_COMMIT__", document.Options.Commit.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__OPTION_NONE__", document.Options.None.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__OPTION_RESTORE__", document.Options.Restore.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__OPTION_SKIP__", document.Options.SkipValidation.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__OPTION_WARMUP__", document.Options.Warmup.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__OPTION_BUILDUP__", document.Options.BuildUp.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__RESTORE_FORMULA__", LeanString(document.Options.RestoreFormula), StringComparison.Ordinal)
            .Replace("__COMMIT_FORMULA__", LeanString(document.Options.CommitFormula), StringComparison.Ordinal)
            .Replace("__COMMIT_BEFORE_FORMULA__", LeanString(document.Options.CommitBeforeFormula), StringComparison.Ordinal)
            .Replace("__CANDIDATE_FORMULA__", LeanString(document.Dispatch.CandidateFormula), StringComparison.Ordinal)
            .Replace("__LOOKUP_FORMULA__", LeanString(document.Dispatch.LookupFormula), StringComparison.Ordinal)
            .Replace("__SIMPLE_FORMULA__", LeanString(document.Dispatch.SimpleFormula), StringComparison.Ordinal)
            .Replace("__GAS_FAILURE_FORMULA__", LeanString(document.Dispatch.AvailableGasFailureFormula), StringComparison.Ordinal)
            .Replace("__GAS_CONDITION__", LeanString(document.Dispatch.Semantics.GasClassification.Condition), StringComparison.Ordinal)
            .Replace("__GAS_SUCCESS_RESULT__", LeanString(document.Dispatch.Semantics.GasClassification.SuccessResult), StringComparison.Ordinal)
            .Replace("__GAS_FAILURE_RESULT__", LeanString(document.Dispatch.Semantics.GasClassification.FailureResult), StringComparison.Ordinal)
            .Replace("__RESTORE_TERM__", terms.Restore, StringComparison.Ordinal)
            .Replace("__COMMIT_TERM__", terms.Commit, StringComparison.Ordinal)
            .Replace("__GAS_SUCCESS_LITERAL__", terms.GasSuccessLiteral, StringComparison.Ordinal)
            .Replace("__GAS_FAILURE_LITERAL__", terms.GasFailureLiteral, StringComparison.Ordinal)
            .Replace("__NO_RECIPIENT_GUARD__", terms.NoRecipientGuard, StringComparison.Ordinal)
            .Replace("__NON_CANDIDATE_GUARD__", terms.NonCandidateGuard, StringComparison.Ordinal)
            .Replace("__SIMPLE_DECISION_LOADED__", terms.SimpleDecisionLoaded, StringComparison.Ordinal)
            .Replace("__FOLLOW_DELEGATION__", terms.FollowDelegation, StringComparison.Ordinal)
            .Replace("__COMMIT_BEFORE_TERM__", terms.CommitBeforeExecution, StringComparison.Ordinal)
            .Replace("__COMMIT_ROOTS__", terms.CommitRoots, StringComparison.Ordinal)
            + "\n";
        if (source.Contains("__", StringComparison.Ordinal) || source.Contains("theorem ", StringComparison.Ordinal) ||
            source.Contains("sorry", StringComparison.Ordinal) || source.Contains("admit", StringComparison.Ordinal) ||
            source.Contains("axiom ", StringComparison.Ordinal))
        {
            throw new ExtractionException("Lean emission retained an unlowered placeholder or proof escape.");
        }

        return new UTF8Encoding(false).GetBytes(source);
    }

    private sealed record LoweredTerms(
        string Restore,
        string Commit,
        string NoRecipientGuard,
        string NonCandidateGuard,
        string SimpleDecisionLoaded,
        string FollowDelegation,
        string CommitBeforeExecution,
        string CommitRoots,
        string GasSuccessLiteral,
        string GasFailureLiteral);

    private static LoweredTerms LowerSourceTerms(IrDocument document)
    {
        SemanticOperation[] operations = document.Dispatch.Semantics.Operations;
        SemanticOperation restore = Operation(operations, SemanticFormula.OptionHasFlag);
        SemanticOperation commit = Operation(operations, SemanticFormula.EffectiveCommit);
        SemanticOperation candidate = Operation(operations, SemanticFormula.CandidateGuard);
        SemanticOperation lookup = Operation(operations, SemanticFormula.CodeLookup);
        SemanticOperation simple = Operation(operations, SemanticFormula.SimpleDecision);
        SemanticOperation commitBefore = Operation(operations, SemanticFormula.CommitBeforeExecution);
        SemanticOperation precommit = Operation(operations, SemanticFormula.PrecommitRequest);
        BranchShape noRecipient = document.Dispatch.Branches.Single(branch => branch.Id == "prepareNoRecipient");
        GasClassification gas = document.Dispatch.Semantics.GasClassification;

        RequireProjectionAgreement(restore.ExpressionAst.Normalized, document.Options.RestoreFormula, "restore option");
        RequireProjectionAgreement(commit.ExpressionAst.Normalized, document.Options.CommitFormula, "effective commit");
        RequireProjectionAgreement(restore.ExpressionAst.Normalized, document.Dispatch.RestoreFormula, "dispatch restore option");
        RequireProjectionAgreement(commit.ExpressionAst.Normalized, document.Dispatch.CommitFormula, "dispatch effective commit");
        RequireProjectionAgreement(candidate.ExpressionAst.Normalized, document.Dispatch.CandidateFormula, "candidate guard");
        RequireProjectionAgreement(lookup.ExpressionAst.Normalized, document.Dispatch.LookupFormula, "code lookup");
        RequireProjectionAgreement(simple.ExpressionAst.Normalized, document.Dispatch.SimpleFormula, "simple decision");
        RequireProjectionAgreement(commitBefore.ExpressionAst.Normalized, document.Dispatch.CommitBeforeFormula, "precommit guard");
        RequireProjectionAgreement(gas.ConditionAst.Normalized, gas.Condition, "available-gas condition");
        RequireProjectionAgreement(gas.SuccessAst.Normalized, gas.SuccessResult, "available-gas success result");
        RequireProjectionAgreement(gas.FailureAst.Normalized, gas.FailureResult, "available-gas failure result");

        string restoreTerm = LowerBoolean(restore.ExpressionAst, EmptyScope);
        string commitTerm = LowerBoolean(commit.ExpressionAst, Scope(("spec.IsEip658Enabled", "eip658Enabled")));
        string noRecipientGuard = LowerBoolean(noRecipient.ConditionAst, Scope(("recipient", "input.tx.to")));
        string nonCandidateGuard = LowerCandidateGuard(candidate.ExpressionAst);
        string simpleDecisionLoaded = LowerBoolean(simple.ExpressionAst, Scope(("delegationAddress", "value.delegation"), ("codeInfo.IsEmpty", "value.codeIsEmpty")));
        string followDelegation = LowerLookupFollow(lookup.ExpressionAst);
        string commitBeforeExecution = LowerBoolean(commitBefore.ExpressionAst,
            Scope(("commit", "commitValue"), ("restore", "restoreValue"), ("simpleTransferRecipient", "simpleRecipient"),
                ("tracer.IsTracingState", "input.tracer.isTracingState")));
        string commitRoots = LowerCommitRoots(precommit.ExpressionAst);
        string gasSuccessLiteral = LowerGasResult(gas.SuccessAst, "success");
        string gasFailureLiteral = LowerGasResult(gas.FailureAst, "failure");

        return new(restoreTerm, commitTerm, noRecipientGuard, nonCandidateGuard, simpleDecisionLoaded, followDelegation,
            commitBeforeExecution, commitRoots, gasSuccessLiteral, gasFailureLiteral);
    }

    private static IReadOnlyDictionary<string, string> EmptyScope { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> Scope(params (string Source, string Lean)[] entries) =>
        entries.ToDictionary(static entry => entry.Source, static entry => entry.Lean, StringComparer.Ordinal);

    private static string LowerCandidateGuard(SourceExpression expression)
    {
        if (!Extractor.MatchesFormulaGrammar(SemanticFormula.CandidateGuard, expression))
        {
            throw new ExtractionException("The candidate guard has no complete typed source lowering.");
        }

        return LowerBoolean(expression, Scope(("recipient", "input.tx.to")));
    }

    private static string LowerLookupFollow(SourceExpression expression)
    {
        if (!Extractor.MatchesFormulaGrammar(SemanticFormula.CodeLookup, expression) || expression.Children.Length != 5 ||
            expression.Children[2].Children.Length != 1)
        {
            throw new ExtractionException("The code lookup has no complete typed source lowering.");
        }

        return LowerBoolean(expression.Children[2].Children[0], EmptyScope);
    }

    private static string LowerCommitRoots(SourceExpression expression)
    {
        if (!Extractor.MatchesFormulaGrammar(SemanticFormula.PrecommitRequest, expression) || expression.Children.Length != 4 ||
            expression.Children[3].Children.Length != 1)
        {
            throw new ExtractionException("The precommit operation has no complete typed source lowering.");
        }

        return LowerLiteral(expression.Children[3].Children[0]);
    }

    private static string LowerGasResult(SourceExpression expression, string arm)
    {
        if (expression.Kind != SourceExpressionKind.MemberAccess || expression.SymbolId != "member:" + expression.Normalized ||
            expression.Children.Length != 1 || !IsIdentifier(expression.Children[0], "TransactionResult") ||
            string.IsNullOrWhiteSpace(expression.Symbol))
        {
            throw new ExtractionException($"The available-gas {arm} result has no complete typed source lowering.");
        }

        return LeanString(expression.Symbol);
    }

    private static string LowerBoolean(SourceExpression expression, IReadOnlyDictionary<string, string> scope)
    {
        ValidateSourceExpression(expression);
        return expression.Kind switch
        {
            SourceExpressionKind.Identifier => LowerIdentifier(expression, scope),
            SourceExpressionKind.Literal when expression.Symbol is "true" or "false" => expression.Symbol,
            SourceExpressionKind.MemberAccess => LowerMemberAccess(expression, scope),
            SourceExpressionKind.Invocation => LowerInvocation(expression, scope),
            SourceExpressionKind.Unary when expression.Symbol == "!" && expression.Children.Length == 1 =>
                "!(" + LowerBoolean(expression.Children[0], scope) + ")",
            SourceExpressionKind.Binary when expression.Symbol is "&&" or "||" or "==" or "!=" or "<" or ">" or "<=" or ">=" &&
                expression.Children.Length == 2 =>
                "(" + LowerBoolean(expression.Children[0], scope) + " " + expression.Symbol + " " +
                LowerBoolean(expression.Children[1], scope) + ")",
            SourceExpressionKind.IsPattern => LowerIsPattern(expression, scope),
            SourceExpressionKind.Parenthesized when expression.Children.Length == 1 => LowerBoolean(expression.Children[0], scope),
            _ => throw new ExtractionException("The typed source expression cannot be lowered as a Boolean term."),
        };
    }

    private static string LowerValue(SourceExpression expression, IReadOnlyDictionary<string, string> scope)
    {
        ValidateSourceExpression(expression);
        return expression.Kind switch
        {
            SourceExpressionKind.Identifier => LowerIdentifier(expression, scope),
            SourceExpressionKind.Literal => LowerLiteral(expression),
            SourceExpressionKind.MemberAccess => LowerMemberAccess(expression, scope),
            SourceExpressionKind.Invocation => LowerInvocation(expression, scope),
            SourceExpressionKind.Unary when expression.Symbol == "!" && expression.Children.Length == 1 =>
                "!(" + LowerBoolean(expression.Children[0], scope) + ")",
            SourceExpressionKind.Binary when expression.Symbol is "&&" or "||" or "==" or "!=" or "<" or ">" or "<=" or ">=" &&
                expression.Children.Length == 2 =>
                "(" + LowerBoolean(expression.Children[0], scope) + " " + expression.Symbol + " " +
                LowerBoolean(expression.Children[1], scope) + ")",
            SourceExpressionKind.Parenthesized when expression.Children.Length == 1 => LowerValue(expression.Children[0], scope),
            SourceExpressionKind.Argument when expression.Children.Length == 1 => LowerValue(expression.Children[0], scope),
            _ => throw new ExtractionException("The typed source expression cannot be lowered as a value term."),
        };
    }

    private static string LowerIdentifier(SourceExpression expression, IReadOnlyDictionary<string, string> scope)
    {
        if (expression.SymbolId != "identifier:" + expression.Symbol)
        {
            throw new ExtractionException("The source identifier no longer carries its admitted identity.");
        }

        if (scope.TryGetValue(expression.Symbol, out string? mapped)) return mapped;

        return expression.Symbol switch
        {
            "true" or "false" => expression.Symbol,
            "tx" => "input.tx",
            "spec" => "input.spec",
            "tracer" => "input.tracer",
            "intrinsicGas" => "input.intrinsic",
            "isCodeOverridable" or "_isCodeOverridable" => "input.isCodeOverridable",
            "ForceSimpleTransferDisabled" => "input.forceSimpleTransferDisabled",
            "recipient" => "input.tx.to",
            "simpleTransferRecipient" => "simpleRecipient",
            "delegationAddress" => "value.delegation",
            "codeInfo" => "value",
            _ => throw new ExtractionException("The source identifier is outside the admitted Lean environment: " + expression.Symbol + "."),
        };
    }

    private static string LowerLiteral(SourceExpression expression)
    {
        if (!expression.SymbolId.StartsWith("literal:", StringComparison.Ordinal) || expression.Symbol is "" or "default")
        {
            throw new ExtractionException("The source literal no longer carries its admitted identity.");
        }

        return expression.Symbol switch
        {
            "true" or "false" or "null" => expression.Symbol,
            _ when expression.Symbol.All(char.IsDigit) => expression.Symbol,
            _ => throw new ExtractionException("The source literal is outside the admitted Lean grammar: " + expression.Symbol + "."),
        };
    }

    private static string LowerMemberAccess(SourceExpression expression, IReadOnlyDictionary<string, string> scope)
    {
        if (expression.Kind != SourceExpressionKind.MemberAccess || expression.Children.Length != 1 ||
            !expression.SymbolId.StartsWith("member:", StringComparison.Ordinal) ||
            !expression.SymbolId.EndsWith("." + expression.Symbol, StringComparison.Ordinal))
        {
            throw new ExtractionException("The source member access no longer carries its admitted identity.");
        }

        SourceExpression receiver = expression.Children[0];
        if (receiver.Kind != SourceExpressionKind.Identifier)
        {
            throw new ExtractionException("The source member receiver is outside the admitted Lean environment.");
        }

        string key = receiver.Symbol + "." + expression.Symbol;
        if (scope.TryGetValue(key, out string? mapped)) return mapped;

        return (receiver.Symbol, expression.Symbol) switch
        {
            ("spec", "IsEip658Enabled") => "eip658Enabled",
            ("spec", "IsEip8037Enabled") => "input.spec.eip8037Enabled",
            ("tracer", "IsTracingState") => "input.tracer.isTracingState",
            ("codeInfo", "IsEmpty") => "value.codeIsEmpty",
            ("tx", "AuthorizationList") => "input.tx.authorizationListPresent",
            ("ExecutionOptions", "Commit") => "executionOptionCommit",
            ("ExecutionOptions", "Restore") => "executionOptionRestore",
            ("ExecutionOptions", "SkipValidation") => "executionOptionSkipValidation",
            ("ExecutionOptions", "Warmup") => "executionOptionWarmup",
            ("ExecutionOptions", "BuildUp") => "executionOptionBuildUp",
            _ => throw new ExtractionException("The source member access is outside the admitted Lean environment: " + key + "."),
        };
    }

    private static string LowerInvocation(SourceExpression expression, IReadOnlyDictionary<string, string> scope)
    {
        if (expression.Kind != SourceExpressionKind.Invocation || expression.Children.Length == 0 ||
            !expression.SymbolId.StartsWith("invocation:", StringComparison.Ordinal) ||
            !expression.SymbolId.EndsWith("#" + expression.Symbol, StringComparison.Ordinal))
        {
            throw new ExtractionException("The source invocation no longer carries its admitted identity.");
        }

        if (expression.Symbol == "HasFlag") return LowerHasFlag(expression);
        if (expression.Symbol == "IsSimpleTransferFastPathCandidate")
        {
            if (!expression.Children.Skip(1).All(static child => child.Kind == SourceExpressionKind.Argument && child.Children.Length == 1))
            {
                throw new ExtractionException("The candidate invocation has incomplete typed arguments.");
            }

            return "((!input.isCodeOverridable && !input.tx.authorizationListPresent) && !input.forceSimpleTransferDisabled)";
        }

        throw new ExtractionException("The source invocation is outside the admitted Lean environment: " + expression.Symbol + ".");
    }

    private static string LowerHasFlag(SourceExpression expression)
    {
        if (expression.Children.Length != 2 || expression.Children[0].Kind != SourceExpressionKind.MemberAccess ||
            expression.Children[0].Children.Length != 1 || !IsIdentifier(expression.Children[0].Children[0], "opts") ||
            expression.Children[1].Kind != SourceExpressionKind.Argument || expression.Children[1].Children.Length != 1 ||
            expression.Children[1].Children[0].Kind != SourceExpressionKind.MemberAccess ||
            expression.Children[1].Children[0].Children.Length != 1 ||
            !IsIdentifier(expression.Children[1].Children[0].Children[0], "ExecutionOptions"))
        {
            throw new ExtractionException("The source HasFlag invocation has no complete typed lowering.");
        }

        string option = expression.Children[1].Children[0].Symbol;
        if (option is not ("Commit" or "Restore" or "SkipValidation"))
        {
            throw new ExtractionException("The source HasFlag option is outside the admitted dispatch vocabulary.");
        }

        return "hasFlag options.raw executionOption" + option;
    }

    private static string LowerIsPattern(SourceExpression expression, IReadOnlyDictionary<string, string> scope)
    {
        if (expression.Children.Length != 2 || expression.Children[1].Kind != SourceExpressionKind.PatternConstant ||
            expression.Children[1].Children.Length != 1 || expression.Children[1].Children[0].Kind != SourceExpressionKind.Literal ||
            expression.Children[1].Children[0].Symbol != "null")
        {
            throw new ExtractionException("The source null-pattern is outside the admitted Lean vocabulary.");
        }

        string value = LowerValue(expression.Children[0], scope);
        if (value == "input.tx.authorizationListPresent") return "!input.tx.authorizationListPresent";
        return value + ".isNone";
    }

    private static bool IsIdentifier(SourceExpression expression, string name) =>
        expression.Kind == SourceExpressionKind.Identifier && expression.Symbol == name &&
        expression.SymbolId == "identifier:" + name;

    private static void ValidateSourceExpression(SourceExpression? expression)
    {
        if (expression is null || string.IsNullOrWhiteSpace(expression.Normalized) ||
            string.IsNullOrWhiteSpace(expression.Symbol) || string.IsNullOrWhiteSpace(expression.SymbolId) ||
            string.IsNullOrWhiteSpace(expression.TypeName) || expression.Children is null ||
            expression.Normalized != expression.Normalized.Trim() || expression.Normalized.Any(char.IsWhiteSpace) ||
            !HasAdmittedExpressionIdentity(expression))
        {
            throw new ExtractionException("Lean emission received an incomplete or relabeled typed source expression.");
        }

        foreach (SourceExpression child in expression.Children)
        {
            ValidateSourceExpression(child);
        }
    }

    private static bool HasAdmittedExpressionIdentity(SourceExpression expression) => expression.Kind switch
    {
        SourceExpressionKind.Identifier => expression.SymbolId == "identifier:" + expression.Symbol,
        SourceExpressionKind.Literal => expression.SymbolId.StartsWith("literal:", StringComparison.Ordinal),
        SourceExpressionKind.Default => expression.Symbol == "default" && expression.SymbolId == "Default:default",
        SourceExpressionKind.MemberAccess => expression.SymbolId.StartsWith("member:", StringComparison.Ordinal) &&
            expression.SymbolId.EndsWith("." + expression.Symbol, StringComparison.Ordinal),
        SourceExpressionKind.Invocation => expression.SymbolId.StartsWith("invocation:", StringComparison.Ordinal) &&
            expression.SymbolId.EndsWith("#" + expression.Symbol, StringComparison.Ordinal),
        SourceExpressionKind.Argument => expression.SymbolId == "argument:" + expression.Symbol,
        SourceExpressionKind.Unary or SourceExpressionKind.Binary or SourceExpressionKind.Conditional or
            SourceExpressionKind.IsPattern or SourceExpressionKind.Assignment or SourceExpressionKind.Parenthesized =>
            expression.SymbolId.StartsWith("operator:", StringComparison.Ordinal),
        SourceExpressionKind.Projection or SourceExpressionKind.Block or SourceExpressionKind.LocalDeclaration or
            SourceExpressionKind.VariableDeclarator or SourceExpressionKind.If or SourceExpressionKind.Return or
            SourceExpressionKind.ExpressionStatement => expression.SymbolId.StartsWith(expression.Kind + ":", StringComparison.Ordinal),
        SourceExpressionKind.PatternConstant or SourceExpressionKind.PatternUnary or SourceExpressionKind.PatternBinary =>
            expression.SymbolId.StartsWith("pattern:", StringComparison.Ordinal),
        _ => false,
    };

    private static string SourceExpressionIdentity(SourceExpression expression) =>
        expression.Kind + "(" + expression.Symbol + ":" + expression.SymbolId + ":" + expression.TypeName + ":" + expression.Normalized + "[" +
        string.Join(',', expression.Children.Select(SourceExpressionIdentity)) + "])");

    private static SemanticOperation Operation(SemanticOperation[] operations, SemanticFormula formula) =>
        operations.Single(operation => operation.Formula == formula);

    private static void RequireProjectionAgreement(string operationSyntax, string projection, string name)
    {
        if (operationSyntax != projection) throw new ExtractionException($"The {name} source projection disagrees with its lowered operation.");
    }

    private static void ValidateSourceLoweredShape(IrDocument document)
    {
        ValidateIrHeader(document);
        if (document.Options is null || document.Dispatch is null || document.Dispatch.Stages is null || document.Dispatch.Branches is null ||
            document.Dispatch.Effects is null || document.Dispatch.Handoffs is null || document.Dispatch.Semantics is null ||
            document.Dispatch.Semantics.Operations is null || document.Dispatch.Semantics.Widths is null ||
            document.Dispatch.Semantics.AdapterPremises is null || document.Dispatch.Semantics.GasClassification is null ||
            document.Dispatch.MethodBindings is null || document.Dispatch.RouteBindings is null || document.Dispatch.PreloadStates is null)
        {
            throw new ExtractionException("The source-derived post-nonce shape is incomplete.");
        }
        string[] stageIds = ["prepareSimpleTransferFastPath", "commitBeforeExecution", "calculateAvailableGas", "dispatchSimpleTransfer", "dispatchEvm"];
        string[] branchIds = ["prepareInitializesPreloadedOutputs", "prepareNoRecipient", "prepareNotCandidate", "lookupEscapes", "lookupReturns", "commitEscapes", "gasRejected", "simpleHandoff", "evmHandoff"];
        TerminalKind[] terminals = [TerminalKind.NoTerminal, TerminalKind.NoTerminal, TerminalKind.NoTerminal, TerminalKind.EscapedLookup, TerminalKind.NoTerminal, TerminalKind.EscapedPrecommit, TerminalKind.GasRejected, TerminalKind.SimpleHandoff, TerminalKind.EvmHandoff];
        if (document.Dispatch.Stages.Length != stageIds.Length || document.Dispatch.Branches.Length != branchIds.Length ||
            document.Dispatch.Effects.Length != 6 || document.Dispatch.Semantics.Operations.Length != 12 ||
            document.Dispatch.Semantics.Widths.Length != 3 || document.Dispatch.Semantics.AdapterPremises.Length != 3 ||
            document.Dispatch.Handoffs.Length != 2 ||
            !document.Dispatch.PreloadStates.SequenceEqual(new[] { PreloadKind.None, PreloadKind.Loaded }))
        {
            throw new ExtractionException("The source-derived post-nonce shape is incomplete.");
        }

        for (int index = 0; index < stageIds.Length; index++)
        {
            StageShape stage = document.Dispatch.Stages[index];
            if (stage is null || stage.Id != stageIds[index] || stage.Ordinal != index + 1)
                throw new ExtractionException("The post-nonce stage order changed before Lean emission.");
            ValidateBinding(stage.Binding);
        }

        for (int index = 0; index < branchIds.Length; index++)
        {
            BranchShape branch = document.Dispatch.Branches[index];
            string[] expectedBranchEffects = index switch
            {
                0 => [nameof(SemanticEffect.InitializePreloadedOutputs)],
                1 or 2 => [nameof(SemanticEffect.AssignSimpleRecipient)],
                3 => [nameof(SemanticEffect.LookupCodeInfo)],
                4 => [nameof(SemanticEffect.LookupCodeInfo), nameof(SemanticEffect.PreserveDelegationDesignator)],
                5 => [nameof(SemanticEffect.RequestCommit)],
                6 => [nameof(SemanticEffect.PreserveFiveZeroGasPolicy)],
                7 => [nameof(SemanticEffect.HandoffSimple)],
                8 => [nameof(SemanticEffect.HandoffEvm)],
                _ => throw new UnreachableException(),
            };
            if (branch is null || branch.Id != branchIds[index] || branch.Ordinal != index + 1 || branch.TerminalKind != terminals[index] ||
                branch.Terminal != branch.TerminalKind.ToString() || branch.Effects is null || branch.Effects.Length == 0 ||
                !branch.Effects.SequenceEqual(expectedBranchEffects) || string.IsNullOrWhiteSpace(branch.Condition) ||
                branch.Binding is null || branch.Condition != branch.Binding.CanonicalSyntax || branch.ConditionAst is null ||
                branch.ConditionAst.Normalized != branch.Condition ||
                !Extractor.SourceExpressionMatchesBinding(branch.Binding, branch.ConditionAst.Normalized, branch.ConditionAst))
            {
                throw new ExtractionException("The post-nonce branch shape changed before Lean emission.");
            }

            ValidateBinding(branch.Binding);
            ValidateSourceExpression(branch.ConditionAst);
        }

        string[] effectIds = ["preloadOutputs", "codeLookup", "precommit", "gasFailure", "simpleHandoff", "evmHandoff"];
        SemanticEffect[] effectKinds =
        [
            SemanticEffect.InitializePreloadedOutputs,
            SemanticEffect.LookupCodeInfo,
            SemanticEffect.RequestCommit,
            SemanticEffect.PreserveFiveZeroGasPolicy,
            SemanticEffect.HandoffSimple,
            SemanticEffect.HandoffEvm,
        ];
        for (int index = 0; index < effectIds.Length; index++)
        {
            EffectShape effect = document.Dispatch.Effects[index];
            if (effect is null || effect.Id != effectIds[index] || effect.Ordinal != index + 1 || effect.Effect != effectKinds[index] ||
                effect.Reads is null || effect.Writes is null || string.IsNullOrWhiteSpace(effect.FailureVisibility))
            {
                throw new ExtractionException("The post-nonce effect shape changed before Lean emission.");
            }

            ValidateBinding(effect.Binding);
        }

        SemanticFormula[] formulas =
        [SemanticFormula.OptionHasFlag, SemanticFormula.EffectiveCommit, SemanticFormula.PrepareFastPath, SemanticFormula.CandidateGuard,
            SemanticFormula.CodeLookup, SemanticFormula.SimpleDecision, SemanticFormula.CommitBeforeExecution, SemanticFormula.PrecommitRequest,
            SemanticFormula.CalculateAvailableGas, SemanticFormula.GasFailurePolicy, SemanticFormula.SimpleHandoff, SemanticFormula.EvmHandoff];
        string[] bindingMembers =
            ["restoreOption", "commitOption", "PrepareSimpleTransferFastPath", "candidateGuard", "codeLookup", "simpleDecision", "commitBeforeExecution", "precommitRequest", "calculateAvailableGas", "gasFailurePolicy", "simpleHandoff", "evmHandoff"];
        for (int index = 0; index < formulas.Length; index++)
        {
            SemanticOperation operation = document.Dispatch.Semantics.Operations[index];
            NumericWidth[] expectedInputWidths = index is 0 or 1 or 6 ? [NumericWidth.Int32] : [];
            string[] expectedSourceOperands = index switch
            {
                0 => ["opts", "ExecutionOptions.Restore"],
                1 => ["opts", "ExecutionOptions.Commit", "ExecutionOptions.SkipValidation", "spec.IsEip658Enabled"],
                2 => ["preloadedCodeInfo", "preloadedDelegationAddress", "recipient"],
                3 => ["recipient", "_isCodeOverridable", "tx.AuthorizationList", "ForceSimpleTransferDisabled"],
                4 => ["recipient", "!spec.IsEip8037Enabled", "spec", "out delegation"],
                5 => ["delegationAddress", "codeInfo.IsEmpty"],
                6 => ["commit", "simpleTransferRecipient", "restore", "tracer.IsTracingState"],
                7 => ["WorldState", "spec", "tracer.IsTracingState ? tracer : NullTxTracer.Instance", "commitRoots:false"],
                8 => ["tx.GasLimit", "intrinsicGas.Standard", "spec", "out gasAvailable"],
                9 => ["GasLimitBelowIntrinsicGas", "available.Value", "available.StateReservoir", "available.StateGasUsed", "available.StateGasSpill", "available.StateGasSpillRefunded"],
                10 => ["tx", "header", "spec", "tracer", "opts", "restore", "commit", "deleteCallerAccount", "recipient", "intrinsic", "gasAvailable", "opcodeGasPrice", "premium", "reserved", "blobBaseFee"],
                11 => ["tx", "header", "spec", "tracer", "opts", "restore", "commit", "deleteCallerAccount", "intrinsic", "gasAvailable", "opcodeGasPrice", "premium", "reserved", "blobBaseFee", "preloadedCodeInfo", "preloadedDelegationAddress"],
                _ => throw new UnreachableException(),
            };
            bool sourceOperandsMatch = operation.SourceOperands is not null && operation.SourceOperands.SequenceEqual(expectedSourceOperands);
            if (index == 0 && operation.SourceOperands is not null)
            {
                sourceOperandsMatch |= operation.SourceOperands.SequenceEqual(
                    ["opts", "ExecutionOptions.Restore", "ExecutionOptions.Commit"]);
            }
            if (index == 1 && operation.SourceOperands is not null)
            {
                sourceOperandsMatch |= operation.SourceOperands.SequenceEqual(
                    ["opts", "ExecutionOptions.Commit", "ExecutionOptions.SkipValidation", "spec.IsEip658Enabled", "ExecutionOptions.Restore"]);
            }
            if (index == 4 && operation.SourceOperands is not null)
            {
                sourceOperandsMatch |= operation.SourceOperands.SequenceEqual(
                    ["recipient", "spec.IsEip8037Enabled", "spec", "out delegation"]);
            }
            if (index == 7 && operation.SourceOperands is not null)
            {
                sourceOperandsMatch |= operation.SourceOperands.SequenceEqual(
                    ["WorldState", "spec", "tracer.IsTracingState ? tracer : NullTxTracer.Instance", "commitRoots:true"]);
            }
            if (operation is null || operation.Id != ExpectedOperationIds[index] || operation.Ordinal != index + 1 || operation.Formula != formulas[index] ||
                operation.InputWidths is null || !operation.InputWidths.SequenceEqual(expectedInputWidths) || !sourceOperandsMatch ||
                operation.Binding is null || operation.Binding.Member != bindingMembers[index] ||
                operation.Expression != operation.Binding.CanonicalSyntax || operation.ExpressionAst is null ||
                operation.Expression != operation.ExpressionAst.Normalized ||
                !Extractor.SourceExpressionMatchesBinding(operation.Binding, operation.ExpressionAst.Normalized, operation.ExpressionAst) ||
                !Extractor.MatchesFormulaGrammar(operation.Formula, operation.ExpressionAst))
            {
                throw new ExtractionException("The post-nonce semantic operation is not source-lowered.");
            }

            ValidateBinding(operation.Binding);
            ValidateSourceExpression(operation.ExpressionAst);
            if (index == 0)
            {
                RequireCanonicalOneOf(operation.Binding.CanonicalSyntax,
                    "The restore option operation has no finite source lowering.",
                    "opts.HasFlag(ExecutionOptions.Restore)",
                    "opts.HasFlag(ExecutionOptions.Restore)||opts.HasFlag(ExecutionOptions.Commit)");
                if (operation.Binding.CanonicalSyntax == "opts.HasFlag(ExecutionOptions.Restore)")
                {
                    ValidateHasFlagBinding(operation.Binding, "opts");
                }
                else if (operation.Binding.NodeKind != nameof(SyntaxKind.LogicalOrExpression) ||
                    operation.Binding.TargetSymbol.Length != 0 || operation.Binding.TargetSymbolKind.Length != 0 || operation.Binding.Receiver.Length != 0)
                {
                    throw new ExtractionException("The alternate restore option lost its binary source-node shape.");
                }
            }
            else if (index == 1)
            {
                RequireCanonicalOneOf(operation.Binding.CanonicalSyntax,
                    "The effective commit operation has no finite source lowering.",
                    "opts.HasFlag(ExecutionOptions.Commit)||(!opts.HasFlag(ExecutionOptions.SkipValidation)&&!spec.IsEip658Enabled)",
                    "opts.HasFlag(ExecutionOptions.Commit)||(!opts.HasFlag(ExecutionOptions.SkipValidation)&&!spec.IsEip658Enabled)||opts.HasFlag(ExecutionOptions.Restore)");
                if (operation.Binding.NodeKind != nameof(SyntaxKind.LogicalOrExpression))
                {
                    throw new ExtractionException("The effective commit operation lost its logical-or syntax node.");
                }
            }
        }

        ValidateGasClassificationForEmission(document.Dispatch.Semantics.GasClassification);

        foreach (WidthShape width in document.Dispatch.Semantics.Widths)
        {
            if (width is null || width.Binding is null || width.SourceDefinition != width.Binding.CanonicalSyntax)
                throw new ExtractionException("The post-nonce width lost its source projection.");
            ValidateBinding(width.Binding);
        }

        foreach (AdapterPremise premise in document.Dispatch.Semantics.AdapterPremises)
        {
            if (premise.Bindings is null || premise.ProjectionAsts is null || premise.DomainAst is null || premise.DomainBinding is null ||
                premise.Bindings.Length == 0 || premise.Bindings.Length != premise.ProjectionAsts.Length || string.IsNullOrWhiteSpace(premise.DomainPredicate))
            {
                throw new ExtractionException("The post-nonce adapter premise is incomplete.");
            }

            ValidateSourceExpression(premise.DomainAst);
            ValidateBinding(premise.DomainBinding);
            if (!Extractor.SourceExpressionMatchesBinding(premise.DomainBinding, premise.DomainAst.Normalized, premise.DomainAst))
            {
                throw new ExtractionException("The post-nonce adapter domain is not source-bound.");
            }

            for (int projectionIndex = 0; projectionIndex < premise.Bindings.Length; projectionIndex++)
            {
                SourceBinding binding = premise.Bindings[projectionIndex];
                SourceExpression projection = premise.ProjectionAsts[projectionIndex];
                ValidateBinding(binding);
                ValidateSourceExpression(projection);
                if (!Extractor.SourceExpressionMatchesBinding(binding, projection.Normalized, projection))
                {
                    throw new ExtractionException("The post-nonce adapter projection is not source-bound.");
                }
            }
        }

        ValidateInvocationBinding(document.Dispatch.Semantics.Operations[4].Binding, "GetCachedCodeInfo", "_codeInfoRepository");
        ValidateInvocationBinding(document.Dispatch.Semantics.Operations[7].Binding, "Commit", "WorldState");
        ValidateInvocationBinding(document.Dispatch.Semantics.Operations[8].Binding, "CalculateAvailableGas", string.Empty);
        ValidateInvocationBinding(document.Dispatch.Semantics.Operations[10].Binding, "ExecuteSimpleTransfer", string.Empty);
        ValidateInvocationBinding(document.Dispatch.Semantics.Operations[11].Binding, "ExecuteEvmTransaction", string.Empty);
        foreach (SourceBinding handoff in document.Dispatch.Handoffs)
        {
            ValidateBinding(handoff);
        }
        ValidateInvocationBinding(document.Dispatch.Handoffs[0], "ExecuteSimpleTransfer", string.Empty);
        ValidateInvocationBinding(document.Dispatch.Handoffs[1], "ExecuteEvmTransaction", string.Empty);
        foreach (SourceBinding binding in document.Dispatch.MethodBindings.Concat(document.Dispatch.RouteBindings))
        {
            ValidateBinding(binding);
            switch (binding.Member)
            {
                case "Process -> ExecuteCore":
                    ValidateInvocationBinding(binding, "ExecuteCore", string.Empty);
                    break;
                case "ExecuteCore -> UseSystemProcessor":
                    ValidateInvocationBinding(binding, "UseSystemProcessor", "SystemTransactionRoutingKernel");
                    break;
                case "ExecuteCore -> Execute(3)":
                    ValidateInvocationBinding(binding, "Execute", string.Empty);
                    break;
                case "Execute(3) -> RecoverSenderBeforeIntrinsicGas":
                    ValidateInvocationBinding(binding, "RecoverSenderBeforeIntrinsicGas", string.Empty);
                    break;
                case "Execute(3) -> CalculateIntrinsicGas":
                    ValidateInvocationBinding(binding, "CalculateIntrinsicGas", string.Empty);
                    break;
                case "Execute(3) -> Execute(6)":
                    ValidateInvocationBinding(binding, "Execute", string.Empty);
                    break;
                case "Execute(6) -> IncrementNonce":
                    ValidateInvocationBinding(binding, "IncrementNonce", string.Empty);
                    break;
                case "codeLookup":
                    ValidateInvocationBinding(binding, "GetCachedCodeInfo", "_codeInfoRepository");
                    break;
                case "precommitRequest":
                    ValidateInvocationBinding(binding, "Commit", "WorldState");
                    break;
                case "calculateAvailableGas":
                    ValidateInvocationBinding(binding, "CalculateAvailableGas", string.Empty);
                    break;
                case "simpleHandoff":
                    ValidateInvocationBinding(binding, "ExecuteSimpleTransfer", string.Empty);
                    break;
                case "evmHandoff":
                    ValidateInvocationBinding(binding, "ExecuteEvmTransaction", string.Empty);
                    break;
            }
        }

        RequireCanonicalOneOf(document.Options.RestoreFormula,
            "The restore option projection has no finite source lowering.",
            "opts.HasFlag(ExecutionOptions.Restore)",
            "opts.HasFlag(ExecutionOptions.Restore)||opts.HasFlag(ExecutionOptions.Commit)");
        RequireCanonicalOneOf(document.Options.CommitFormula,
            "The effective commit projection has no finite source lowering.",
            "opts.HasFlag(ExecutionOptions.Commit)||(!opts.HasFlag(ExecutionOptions.SkipValidation)&&!spec.IsEip658Enabled)",
            "opts.HasFlag(ExecutionOptions.Commit)||(!opts.HasFlag(ExecutionOptions.SkipValidation)&&!spec.IsEip658Enabled)||opts.HasFlag(ExecutionOptions.Restore)");
        RequireCanonicalOneOf(document.Dispatch.CommitBeforeFormula,
            "The precommit guard was not source-lowered.",
            "commit&&(simpleTransferRecipientisnull||restore||tracer.IsTracingState)",
            "commit||(simpleTransferRecipientisnull||restore||tracer.IsTracingState)");
        RequireCanonicalOneOf(document.Dispatch.CandidateFormula,
            "The candidate guard was not source-lowered.",
            "recipientisnull||!IsSimpleTransferFastPathCandidate(tx,_isCodeOverridable)",
            "recipientisnull||IsSimpleTransferFastPathCandidate(tx,_isCodeOverridable)");
        RequireCanonicalOneOf(document.Dispatch.LookupFormula,
            "The lookup shape was not source-lowered.",
            "_codeInfoRepository.GetCachedCodeInfo(recipient,followDelegation:!spec.IsEip8037Enabled,spec,outpreloadedDelegationAddress)",
            "_codeInfoRepository.GetCachedCodeInfo(recipient,followDelegation:spec.IsEip8037Enabled,spec,outpreloadedDelegationAddress)");
        if (document.Dispatch.AvailableGasFailureFormula != "available=default")
            throw new ExtractionException("The post-nonce gas-failure formula cannot be lowered.");
        RequireCanonicalOneOf(document.Dispatch.SimpleFormula,
            "The simple-decision formula was not source-lowered.",
            "delegationAddressisnull&&codeInfo.IsEmpty",
            "delegationAddressisnull||codeInfo.IsEmpty");
    }

    private static void ValidateGasClassificationForEmission(GasClassification classification)
    {
        RequireCanonicalOneOf(classification.Condition,
            "The available-gas condition has no finite source lowering.",
            "TGasPolicy.TryCreateAvailableFromIntrinsic(tx.GasLimit,intrinsicGas.Standard,spec,outgasAvailable)");
        if (classification.ConditionBinding is null || classification.SuccessBinding is null || classification.FailureBinding is null ||
            classification.ConditionBinding.Member != "CalculateAvailableGas.condition" ||
            classification.SuccessBinding.Member != "CalculateAvailableGas.success" ||
            classification.FailureBinding.Member != "CalculateAvailableGas.failure" ||
            classification.ConditionBinding.CanonicalSyntax != classification.Condition ||
            classification.SuccessBinding.CanonicalSyntax != classification.SuccessResult ||
            classification.FailureBinding.CanonicalSyntax != classification.FailureResult ||
            classification.ConditionAst is null || classification.SuccessAst is null || classification.FailureAst is null ||
            classification.ConditionAst.Normalized != classification.Condition ||
            classification.SuccessAst.Normalized != classification.SuccessResult ||
            classification.FailureAst.Normalized != classification.FailureResult ||
            !Extractor.SourceExpressionMatchesBinding(classification.ConditionBinding, classification.ConditionAst.Normalized, classification.ConditionAst) ||
            !Extractor.SourceExpressionMatchesBinding(classification.SuccessBinding, classification.SuccessAst.Normalized, classification.SuccessAst) ||
            !Extractor.SourceExpressionMatchesBinding(classification.FailureBinding, classification.FailureAst.Normalized, classification.FailureAst))
        {
            throw new ExtractionException("The available-gas result bindings no longer project their admitted source nodes.");
        }

        ValidateBinding(classification.ConditionBinding);
        ValidateBinding(classification.SuccessBinding);
        ValidateBinding(classification.FailureBinding);
        ValidateSourceExpression(classification.ConditionAst);
        ValidateSourceExpression(classification.SuccessAst);
        ValidateSourceExpression(classification.FailureAst);
        ValidateInvocationBinding(classification.ConditionBinding, "TryCreateAvailableFromIntrinsic", "TGasPolicy");
        ValidateGasResultBinding(classification.SuccessBinding, classification.SuccessResult, classification.SuccessAst);
        ValidateGasResultBinding(classification.FailureBinding, classification.FailureResult, classification.FailureAst);
    }

    private static void ValidateGasResultBinding(SourceBinding binding, string sourceResult, SourceExpression resultAst)
    {
        if (resultAst.Kind != SourceExpressionKind.MemberAccess || resultAst.Children.Length != 1 ||
            !IsIdentifier(resultAst.Children[0], "TransactionResult") ||
            resultAst.Normalized != sourceResult || resultAst.SymbolId != "member:" + resultAst.Normalized)
        {
            throw new ExtractionException("The available-gas result has no source-bound member expression.");
        }

        string member = resultAst.Symbol;
        if (binding.NodeKind != nameof(SyntaxKind.SimpleMemberAccessExpression) ||
            !binding.TargetSymbol.EndsWith("TransactionResult." + member, StringComparison.Ordinal))
        {
            throw new ExtractionException("The available-gas result binding no longer resolves the admitted TransactionResult member.");
        }
    }

    private static void ValidateIrHeader(IrDocument document)
    {
        if (document.SchemaVersion != 1 || document.Kernel != "Nethermind ordinary standard-mainnet post-nonce dispatch" ||
            document.AcceptanceState != "bounded-source-extraction-and-refinement-only" || !IsSha256(document.SourceClosureSha256))
            throw new ExtractionException("The generated post-nonce IR header changed.");
    }

    private static string[] ExpectedOperationIds =>
    ["restoreOption", "commitOption", "prepareFastPath", "candidateGuard", "codeLookup", "simpleDecision", "commitBeforeExecution", "precommitRequest", "calculateAvailableGas", "gasFailurePolicy", "simpleHandoff", "evmHandoff"];

    private static string Quoted(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(LeanString)) + "]";

    private static string BindingIdentity(SourceBinding binding) => binding.Path + "|" + binding.Owner + "|" + binding.Member + "|" + binding.Signature + "|" + binding.NodeKind + "|" + binding.TokenSha256 + "|" + binding.CanonicalSyntaxSha256 + "|" + binding.SourceSyntax + "|" + binding.OperationKind + "|" + binding.OperationType + "|" + binding.OperationIsImplicit + "|" + binding.DataFlowSucceeded + "|" + string.Join(',', binding.ReadInside) + "|" + string.Join(',', binding.WrittenInside) + "|" + string.Join(',', binding.ReadOutside) + "|" + string.Join(',', binding.WrittenOutside) + "|" + binding.ContainingMember + "|" + binding.Receiver + "|" + binding.TargetSymbol + "|" + binding.TargetSymbolKind + "|" + binding.CandidateReason + "|" + binding.IsErrorSymbol + "|" + binding.HasCandidateSymbols + "|" + binding.StatementOrdinal + "|" + binding.ControlFlowPath + "|" + binding.ControlFlowBlock;

    private static string LeanString(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static void RequireCanonicalOneOf(string actual, string message, params string[] admittedForms)
    {
        if (!admittedForms.Contains(actual, StringComparer.Ordinal)) throw new ExtractionException(message);
    }

    private static void ValidateBinding(SourceBinding binding)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.Path) || string.IsNullOrWhiteSpace(binding.Owner) ||
            string.IsNullOrWhiteSpace(binding.Member) || string.IsNullOrWhiteSpace(binding.Signature) || string.IsNullOrWhiteSpace(binding.NodeKind) ||
            string.IsNullOrWhiteSpace(binding.CanonicalSyntax) || string.IsNullOrWhiteSpace(binding.SourceSyntax) ||
            string.IsNullOrWhiteSpace(binding.OperationKind) || !binding.OperationKind.StartsWith("Syntax:", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(binding.OperationType) ||
            string.IsNullOrWhiteSpace(binding.ContainingMember) || binding.Receiver is null || binding.TargetSymbol is null ||
            binding.ReadInside is null || binding.WrittenInside is null || binding.ReadOutside is null || binding.WrittenOutside is null ||
            string.IsNullOrWhiteSpace(binding.CandidateReason) || binding.CandidateReason != "None" ||
            binding.IsErrorSymbol || binding.HasCandidateSymbols ||
            binding.TargetSymbolKind == "ErrorType" || binding.TargetSymbol.Contains("<error", StringComparison.OrdinalIgnoreCase) ||
            binding.TargetSymbol.Length != 0 && string.IsNullOrWhiteSpace(binding.TargetSymbolKind) ||
            binding.StatementOrdinal < 0 || binding.ControlFlowBlock < -1 || !IsSha256(binding.TokenSha256) || !IsSha256(binding.CanonicalSyntaxSha256) ||
            binding.CanonicalSyntaxSha256 != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(binding.CanonicalSyntax))) ||
            binding.ReadInside.Distinct(StringComparer.Ordinal).Count() != binding.ReadInside.Length ||
            binding.WrittenInside.Distinct(StringComparer.Ordinal).Count() != binding.WrittenInside.Length ||
            binding.ReadOutside.Distinct(StringComparer.Ordinal).Count() != binding.ReadOutside.Length ||
            binding.WrittenOutside.Distinct(StringComparer.Ordinal).Count() != binding.WrittenOutside.Length)
            throw new ExtractionException("Lean emission received an incomplete source binding.");
    }

    private static void ValidateInvocationBinding(SourceBinding binding, string targetMember, string receiver)
    {
        if (binding.NodeKind != nameof(SyntaxKind.InvocationExpression) ||
            !binding.TargetSymbol.Contains("." + targetMember, StringComparison.Ordinal) ||
            receiver.Length != 0 && !binding.Receiver.Contains(receiver, StringComparison.Ordinal))
        {
            throw new ExtractionException($"Lean emission received a binding for {targetMember} with the wrong receiver/overload.");
        }
    }

    private static void ValidateHasFlagBinding(SourceBinding binding, string receiver)
    {
        ValidateInvocationBinding(binding, "HasFlag", receiver);
        if (!binding.TargetSymbol.Contains("System.Enum.HasFlag(System.Enum)", StringComparison.Ordinal))
        {
            throw new ExtractionException("Lean emission received a binding for a different HasFlag overload.");
        }
    }

    private static bool IsSha256(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private const string LeanSource = """
-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only
-- Generated by OrdinaryPostNonceDispatchExtractor. Do not edit.
-- This file is the executable, bounded post-IncrementNonce dispatch model.

import Lean

namespace OrdinaryPostNonceDispatchExtractor.Generated

def sourceIrSha256 : String := "__IR_SHA256__"
def sourceStages : List String := __STAGES__
def sourceBranches : List String := __BRANCHES__
def sourceEffects : List String := __EFFECTS__
def sourcePreloadStates : List String := __PRELOAD_STATES__
def sourceOperations : List String := __OPERATIONS__
def sourceBindings : List String := __BINDINGS__
def sourceAdapters : List String := __ADAPTERS__
def sourceRestoreFormula : String := __RESTORE_FORMULA__
def sourceCommitFormula : String := __COMMIT_FORMULA__
def sourceCommitBeforeFormula : String := __COMMIT_BEFORE_FORMULA__
def sourceCandidateFormula : String := __CANDIDATE_FORMULA__
def sourceLookupFormula : String := __LOOKUP_FORMULA__
def sourceSimpleFormula : String := __SIMPLE_FORMULA__
def sourceGasFailureFormula : String := __GAS_FAILURE_FORMULA__
def sourceGasCondition : String := __GAS_CONDITION__
def sourceGasSuccessResult : String := __GAS_SUCCESS_RESULT__
def sourceGasFailureResult : String := __GAS_FAILURE_RESULT__

def executionOptionCommit : Nat := __OPTION_COMMIT__
def executionOptionNone : Nat := __OPTION_NONE__
def executionOptionRestore : Nat := __OPTION_RESTORE__
def executionOptionSkipValidation : Nat := __OPTION_SKIP__
def executionOptionWarmup : Nat := __OPTION_WARMUP__
def executionOptionBuildUp : Nat := __OPTION_BUILDUP__

structure Options where
  raw : Nat
  deriving DecidableEq, Repr

def hasFlag (raw flag : Nat) : Bool := flag != 0 && ((raw / flag) % 2 == 1)
def restore (options : Options) : Bool := __RESTORE_TERM__
def commit (options : Options) (eip658Enabled : Bool) : Bool := __COMMIT_TERM__

structure Transaction where
  id : Nat
  to : Option Nat
  authorizationListPresent : Bool
  deriving DecidableEq, Repr

structure Header where
  id : Nat
  deriving DecidableEq, Repr

structure Spec where
  id : Nat
  eip8037Enabled : Bool
  eip658Enabled : Bool
  deriving DecidableEq, Repr

structure Tracer where
  id : Nat
  isTracingState : Bool
  deriving DecidableEq, Repr

structure Intrinsic where
  standard : Nat
  deriving DecidableEq, Repr

structure GasPolicy where
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

def zeroGas : GasPolicy :=
  { value := 0, stateReservoir := 0, stateGasUsed := 0, stateGasSpill := 0, stateGasSpillRefunded := 0 }

structure LoadedPreload where
  codeHandle : Nat
  delegation : Option Nat
  codeIsEmpty : Bool
  deriving DecidableEq, Repr

inductive Preload where
  | none
  | loaded (value : LoadedPreload)
  deriving DecidableEq, Repr

def simpleDecision : Preload → Bool
  | .none => false
  | .loaded value => __SIMPLE_DECISION_LOADED__

structure LookupResponse where
  escaped : Bool
  preload : Preload
  deriving DecidableEq, Repr

structure LookupRequest where
  recipient : Nat
  followDelegation : Bool
  spec : Spec
  deriving DecidableEq, Repr

inductive CommitTracer where
  | tracing (value : Tracer)
  | null
  deriving DecidableEq, Repr

structure CommitRequest where
  spec : Spec
  tracer : CommitTracer
  commitRoots : Bool
  deriving DecidableEq, Repr

structure CommitResponse where
  escaped : Bool
  deriving DecidableEq, Repr

structure AvailableGasResponse where
  accepted : Bool
  gas : GasPolicy
  deriving DecidableEq, Repr

def classifyAvailableGas (condition : Bool) : String :=
  if condition then __GAS_SUCCESS_LITERAL__ else __GAS_FAILURE_LITERAL__

structure Input where
  tx : Transaction
  header : Header
  spec : Spec
  tracer : Tracer
  options : Options
  intrinsic : Intrinsic
  gasLimit : Nat
  isCodeOverridable : Bool
  forceSimpleTransferDisabled : Bool
  lookup : LookupResponse
  commitResponse : CommitResponse
  availableGas : AvailableGasResponse
  opcodePrice : Nat
  premium : Nat
  reserved : Nat
  blobBaseFee : Nat
  deleteCallerAccount : Bool
  deriving DecidableEq, Repr

structure Handoff where
  tx : Transaction
  header : Header
  spec : Spec
  tracer : Tracer
  opts : Options
  restore : Bool
  commit : Bool
  deleteCallerAccount : Bool
  intrinsic : Intrinsic
  gasAvailable : GasPolicy
  opcodePrice : Nat
  premium : Nat
  reserved : Nat
  blobBaseFee : Nat
  preload : Preload
  recipient : Option Nat
  deriving DecidableEq, Repr

inductive TerminalOutcome where
  | EscapedLookup
  | EscapedPrecommit
  | GasRejected
  | SimpleHandoff
  | EvmHandoff
  deriving DecidableEq, Repr

structure Result where
  outcome : TerminalOutcome
  preload : Preload
  gasAvailable : GasPolicy
  handoff : Option Handoff
  lookupRequest : Option LookupRequest
  commitRequest : Option CommitRequest
  failure : Option String
  deriving DecidableEq, Repr

structure Prepared where
  recipient : Option Nat
  preload : Preload
  simpleRecipient : Option Nat
  lookupEscaped : Bool
  lookupRequest : Option LookupRequest
  deriving DecidableEq, Repr

def prepare (input : Input) : Prepared :=
  -- The two source out parameters begin as null, represented by one coherent Preload.none.
  if __NO_RECIPIENT_GUARD__ then
    { recipient := none, preload := .none, simpleRecipient := none, lookupEscaped := false, lookupRequest := none }
  else
    match input.tx.to with
    | none => { recipient := none, preload := .none, simpleRecipient := none, lookupEscaped := false, lookupRequest := none }
    | some recipient =>
      if __NON_CANDIDATE_GUARD__ then
        { recipient := some recipient, preload := .none, simpleRecipient := none, lookupEscaped := false, lookupRequest := none }
      else
        let request : Option LookupRequest := some { recipient := recipient, followDelegation := __FOLLOW_DELEGATION__, spec := input.spec }
        if input.lookup.escaped then
          { recipient := some recipient, preload := .none, simpleRecipient := none, lookupEscaped := true, lookupRequest := request }
        else
          let returned := input.lookup.preload
          let simple := if simpleDecision returned then some recipient else none
          { recipient := some recipient, preload := returned, simpleRecipient := simple, lookupEscaped := false, lookupRequest := request }

def commitRequest (input : Input) : CommitRequest :=
  { spec := input.spec,
    tracer := if input.tracer.isTracingState then .tracing input.tracer else .null,
    commitRoots := __COMMIT_ROOTS__ }

def commitBeforeExecution (input : Input) (simpleRecipient : Option Nat) (restoreValue : Bool) (commitValue : Bool) : Bool :=
  __COMMIT_BEFORE_TERM__

def handoff (input : Input) (prepared : Prepared) (gas : GasPolicy) (simpleRecipient : Option Nat) (restoreValue commitValue : Bool) : Handoff :=
  { tx := input.tx, header := input.header, spec := input.spec, tracer := input.tracer, opts := input.options,
    restore := restoreValue, commit := commitValue, deleteCallerAccount := input.deleteCallerAccount,
    intrinsic := input.intrinsic, gasAvailable := gas, opcodePrice := input.opcodePrice, premium := input.premium,
    reserved := input.reserved, blobBaseFee := input.blobBaseFee, preload := prepared.preload, recipient := simpleRecipient }

def run (input : Input) : Result :=
  let prepared := prepare input
  if prepared.lookupEscaped then
    { outcome := .EscapedLookup, preload := prepared.preload, gasAvailable := zeroGas, handoff := none,
      lookupRequest := prepared.lookupRequest, commitRequest := none, failure := none }
  else
    let restoreValue := restore input.options
    let commitValue := commit input.options input.spec.eip658Enabled
    let commitBefore := commitBeforeExecution input prepared.simpleRecipient restoreValue commitValue
    if commitBefore && input.commitResponse.escaped then
      { outcome := .EscapedPrecommit, preload := prepared.preload, gasAvailable := zeroGas, handoff := none,
        lookupRequest := prepared.lookupRequest, commitRequest := some (commitRequest input), failure := none }
    else if classifyAvailableGas input.availableGas.accepted == __GAS_SUCCESS_LITERAL__ then
      let h := handoff input prepared input.availableGas.gas prepared.simpleRecipient restoreValue commitValue
      let request := if commitBefore then some (commitRequest input) else none
      match prepared.simpleRecipient with
       | some _ =>
         { outcome := .SimpleHandoff, preload := prepared.preload, gasAvailable := input.availableGas.gas,
           handoff := some h, lookupRequest := prepared.lookupRequest, commitRequest := request, failure := none }
       | none =>
         { outcome := .EvmHandoff, preload := prepared.preload, gasAvailable := input.availableGas.gas,
           handoff := some h, lookupRequest := prepared.lookupRequest, commitRequest := request, failure := none }
    else
      let request := if commitBefore then some (commitRequest input) else none
      { outcome := .GasRejected, preload := prepared.preload, gasAvailable := zeroGas, handoff := none,
        lookupRequest := prepared.lookupRequest, commitRequest := request,
        failure := some (classifyAvailableGas input.availableGas.accepted) }

end OrdinaryPostNonceDispatchExtractor.Generated
""";
}
