// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

/// <summary>Emits theorem-free Lean model definitions from the finite completion IR.</summary>
/// <remarks>
/// Only a closed set of source classifications is lowered to executable terms. The classifications
/// are read from the typed operation lowering in the IR; source bindings and fingerprints are also
/// emitted as audit data, while the independent module proves only the generated-model versus
/// handwritten-model relation. This emitter does not claim that those terms execute production
/// bodies or callback/world adapters.
/// </remarks>
internal static class LeanEmitter
{
    internal static byte[] Emit(IrDocument document, string irSha256)
    {
        Validate(document);
        string source = Template
            .Replace("__IR_SHA256__", irSha256, StringComparison.Ordinal)
            .Replace("__ACCEPTANCE_STATE__", LeanString(document.AcceptanceState), StringComparison.Ordinal)
            .Replace("__SOURCE_CLOSURE__", document.SourceClosureSha256, StringComparison.Ordinal)
            .Replace("__HANDOFF_SHA__", DependencySha(document, Extractor.HandoffLeanPath), StringComparison.Ordinal)
            .Replace("__SETTLEMENT_SHA__", DependencySha(document, Extractor.SettlementLeanPath), StringComparison.Ordinal)
            .Replace("__STATE_CHARGE_SHA__", DependencySha(document, Extractor.StateChargeLeanPath), StringComparison.Ordinal)
            .Replace("__ROUTING_SHA__", DependencySha(document, Extractor.RoutingLeanPath), StringComparison.Ordinal)
            .Replace("__STAGES__", QuoteList(document.Completion.Stages.Select(static stage => stage.Ordinal + ":" + stage.Id + ":" + stage.Kind)), StringComparison.Ordinal)
            .Replace("__BRANCHES__", QuoteList(document.Completion.Branches.Select(static branch => branch.Ordinal + ":" + branch.Id + ":" + branch.Condition)), StringComparison.Ordinal)
            .Replace("__EFFECTS__", QuoteList(document.Completion.Effects.Select(static effect => effect.Ordinal + ":" + effect.Id + ":" + effect.Kind)), StringComparison.Ordinal)
            .Replace("__OPERATIONS__", QuoteList(document.Completion.Operations.Select(static operation => operation.Ordinal + ":" + operation.Id + ":" + operation.Formula + ":" + operation.Lowering.Grammar)), StringComparison.Ordinal)
            .Replace("__ADAPTERS__", QuoteList(document.Completion.Adapters.Select(static adapter => adapter.Id + ":" + adapter.Kind + ":" + adapter.CoherencePredicate)), StringComparison.Ordinal)
            .Replace("__BINDINGS__", QuoteList(AllBindings(document).Select(BindingIdentity)), StringComparison.Ordinal)
            .Replace("__NEW_ACCOUNT_COST__", document.Completion.Lowering.NewAccountStateCost.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__TRANSFER_LOG_ADDRESS__", document.Completion.Lowering.TransferLogAddress, StringComparison.Ordinal)
            .Replace("__TRANSFER_SIGNATURE__", document.Completion.Lowering.TransferSignature, StringComparison.Ordinal)
            .Replace("__TRANSFER_LOG_PAYLOAD__", LeanString(document.Completion.Lowering.TransferLogPayloadGrammar), StringComparison.Ordinal)
            .Replace("__NO_FRAME_GOTO_COUNT__", document.Completion.Lowering.NoFrameGotoCount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__NO_FRAME_CFG_PROVEN__", BoolLiteral(document.Completion.Lowering.NoFrameCfgProven), StringComparison.Ordinal)
            .Replace("__FAIL_CREATE_BYPASS__", BoolLiteral(document.Completion.Lowering.FailContractCreateBypassesNoFrame), StringComparison.Ordinal)
            .Replace("__NO_FRAME_CFG_PATHS__", QuoteList(document.Completion.Lowering.NoFrameCfgPaths), StringComparison.Ordinal)
            .Replace("__PREDICATES__", EmitPredicates(document), StringComparison.Ordinal)
            .Replace("__OPERATION_SET__", EmitOperationSet(document), StringComparison.Ordinal)
            .Replace("__OPERATION_DEFINITIONS__", EmitOperationDefinitions(document), StringComparison.Ordinal)
            .Replace("__FORMULAS__", QuoteList(document.Completion.Operations.Select(static operation => operation.Formula).Distinct(StringComparer.Ordinal)), StringComparison.Ordinal)
            .Replace("__RUN__", EmitRun(document), StringComparison.Ordinal)
            + "\n";
        int placeholder = source.IndexOf("__", StringComparison.Ordinal);
        if (placeholder >= 0 || ContainsLeanCodeToken(source, "sorry") || ContainsLeanCodeToken(source, "admit") ||
            ContainsLeanCodeToken(source, "axiom"))
        {
            int lineStart = source.LastIndexOf('\n', Math.Max(0, placeholder - 1)) + 1;
            int lineEnd = source.IndexOf('\n', Math.Max(0, placeholder));
            if (lineEnd < 0) lineEnd = source.Length;
            string line = placeholder >= 0 ? source[lineStart..lineEnd] : "(proof escape)";
            throw new ExtractionException($"Lean emission retained an unlowered placeholder or proof escape: {line}");
        }
        return new UTF8Encoding(false).GetBytes(source);

        static bool ContainsLeanCodeToken(string text, string token) =>
            text.Split('\n').Any(line =>
            {
                int comment = line.IndexOf("--", StringComparison.Ordinal);
                string code = comment >= 0 ? line[..comment] : line;
                int start = 0;
                while ((start = code.IndexOf(token, start, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    bool leftBoundary = start == 0 || !char.IsLetterOrDigit(code[start - 1]) && code[start - 1] != '_';
                    int end = start + token.Length;
                    bool rightBoundary = end == code.Length || !char.IsLetterOrDigit(code[end]) && code[end] != '_';
                    if (leftBoundary && rightBoundary) return true;
                    start = end;
                }
                return false;
            });
    }

    private static void Validate(IrDocument document)
    {
        if (document.Completion is null || document.Completion.Lowering is null || document.Completion.Stages.Length != 13 || document.Completion.Effects.Length != 19 ||
            document.Completion.Operations.Length != 15 || document.Completion.Adapters.Length != 6)
            throw new ExtractionException("The simple-transfer IR cannot be lowered to its finite Lean schema.");
        if (document.AcceptanceState != "hash-pinned-audited-handwritten-model-to-model-request-boundary-only")
            throw new ExtractionException("The simple-transfer IR does not declare the request-boundary-only model claim.");
        string[] formulas = ["unconditionalMetric", "newAccountCost", "stateChargeResult", "payValue", "recipientWrite", "actionStart", "clearGas", "transferLog", "substate", "settlement", "access", "headerFees", "finalize", "receiptFailure", "receiptSuccess"];
        if (document.Completion.Operations.Any(operation => !formulas.Contains(operation.Formula, StringComparer.Ordinal) ||
            operation.Lowering is null || operation.Lowering.Formula != operation.Formula ||
            string.IsNullOrWhiteSpace(operation.Lowering.TargetSymbolIdentity) ||
            string.IsNullOrWhiteSpace(operation.Lowering.ExecutionTerm) ||
            operation.Lowering.TargetSymbolIdentity != operation.Binding.TargetSymbolIdentity ||
            operation.Lowering.ArgumentNames.Length != operation.Lowering.ArgumentTypes.Length ||
            operation.Lowering.ArgumentNames.Length != operation.Lowering.ArgumentKinds.Length))
            throw new ExtractionException("The simple-transfer IR contains an unsupported semantic formula.");
        if (document.Completion.Lowering.NewAccountStateCost <= 0 ||
            document.Completion.Lowering.NoFrameGotoCount != 4 ||
            !document.Completion.Lowering.NoFrameCfgProven ||
            !document.Completion.Lowering.FailContractCreateBypassesNoFrame)
            throw new ExtractionException("The simple-transfer IR is missing its source CFG and schedule-admission facts.");
    }

    private static IEnumerable<SourceBinding> AllBindings(IrDocument document) =>
        document.Completion.MethodBindings
            .Concat(document.Completion.RouteBindings)
            .Concat(document.Completion.Stages.Select(static stage => stage.Binding))
            .Concat(document.Completion.Branches.Select(static branch => branch.Binding))
            .Concat(document.Completion.Effects.Select(static effect => effect.Binding))
            .Concat(document.Completion.Operations.Select(static operation => operation.Binding))
            .Concat(document.Completion.Adapters.SelectMany(static adapter => adapter.Bindings))
            .Distinct();

    private static string DependencySha(IrDocument document, string path) =>
        document.Dependencies.Single(dependency => dependency.Path == path).Sha256;

    private static string BindingIdentity(SourceBinding binding) =>
        binding.Path + "|" + binding.Owner + "|" + binding.Member + "|" + binding.OperationKind + "|" +
        binding.CanonicalSyntaxSha256 + "|" + binding.TargetSymbol + "|identity=" + binding.TargetSymbolIdentity + "|resolved=" + binding.SymbolResolved +
        "|typedAst=" + TypedAstFingerprint(binding.TypedAst);

    private static string TypedAstFingerprint(TypedAstNode node)
    {
        StringBuilder canonical = new();
        Append(node, canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));

        static void Append(TypedAstNode current, StringBuilder destination)
        {
            destination.Append(current.Kind.Length).Append(':').Append(current.Kind)
                .Append(current.Name.Length).Append(':').Append(current.Name)
                .Append(current.Symbol.Length).Append(':').Append(current.Symbol)
                .Append(current.Constant.Length).Append(':').Append(current.Constant)
                .Append(current.Type.Length).Append(':').Append(current.Type)
                .Append(current.TypeIdentity.Length).Append(':').Append(current.TypeIdentity)
                .Append(current.ParameterName.Length).Append(':').Append(current.ParameterName)
                .Append(current.ArgumentKind.Length).Append(':').Append(current.ArgumentKind)
                .Append(current.RefKind.Length).Append(':').Append(current.RefKind)
                .Append('[');
            foreach (TypedAstNode child in current.Children) Append(child, destination);
            destination.Append(']');
        }
    }

    private static string BoolLiteral(bool value) => value ? "true" : "false";

    private static string EmitPredicates(IrDocument document)
    {
        BranchShape Branch(string id) => document.Completion.Branches.Single(branch => branch.Id == id);
        SourceBinding BindingRole(string role) => AllBindings(document).Single(binding => binding.Role == role);
        return string.Join('\n',
            "def sourceNewAccountPredicate : SourcePredicate := " + EmitPredicate(Branch("newAccountCharge").Binding.TypedAst),
            "def sourceRecipientPredicate : SourcePredicate := " + EmitPredicate(Branch("recipientWrites").Binding.TypedAst),
            "def sourceActionsPredicate : SourcePredicate := " + EmitPredicate(Branch("actions").Binding.TypedAst),
            "def sourceLogPredicate : SourcePredicate := " + EmitPredicate(Branch("transferLogs").Binding.TypedAst),
            "def sourceCountersPredicate : SourcePredicate := " + EmitPredicate(Branch("counterMode").Binding.TypedAst),
            "def sourceRestorePredicate : SourcePredicate := " + EmitPredicate(Branch("finalizationMode").Binding.TypedAst),
            "def sourceCommitPredicate : SourcePredicate := " + EmitPredicate(BindingRole("commit branch").TypedAst),
            "def sourceReceiptPredicate : SourcePredicate := " + EmitPredicate(Branch("receipt").Binding.TypedAst),
            "def sourceAccessHotPredicate : SourcePredicate := " + EmitPredicate(BindingRole("hot/cold access guard").TypedAst),
            "def sourceAccessListPredicate : SourcePredicate := " + EmitPredicate(BindingRole("transaction access-list guard").TypedAst),
            "def sourceAccessCoinbasePredicate : SourcePredicate := " + EmitPredicate(BindingRole("coinbase access guard").TypedAst),
            "def sourceAccessRecipientPredicate : SourcePredicate := " + EmitPredicate(BindingRole("recipient access guard").TypedAst),
            "def sourceBuildUpPredicate : SourcePredicate := " + EmitPredicate(BindingRole("exact build-up branch").TypedAst));
    }

    private static string EmitPredicate(TypedAstNode original)
    {
        TypedAstNode node = original;
        while (node.Kind is "Parenthesized" or "Conversion" && node.Children.Length == 1) node = node.Children[0];
        if (node.Kind is "Binary" && node.Children.Length == 2)
        {
            string constructor = node.Name switch
            {
                "ConditionalAnd" or "LogicalAnd" or "And" => "all",
                "ConditionalOr" or "LogicalOr" or "Or" => "any",
                "Equals" or "Equal" when IsBuildUpComparison(node) => "buildUp",
                _ => string.Empty,
            };
            if (constructor == "all" || constructor == "any")
                return $".{constructor} ({EmitPredicate(node.Children[0])}) ({EmitPredicate(node.Children[1])})";
            if (constructor == "buildUp") return ".buildUp";
        }
        if (node.Kind == "Unary" && node.Children.Length == 1 && (node.Name is "Not" or "LogicalNot"))
            return $".negate ({EmitPredicate(node.Children[0])})";

        string? primitive = PredicatePrimitive(node);
        if (primitive is not null) return "." + primitive;
        throw new ExtractionException($"Unsupported source predicate AST node {node.Kind}/{node.Name}; executable lowering is not closed.");
    }

    private static string? PredicatePrimitive(TypedAstNode node)
    {
        if (node.Kind == "Literal")
            return node.Constant is "True" or "true" or "1" ? "literalTrue" : node.Constant is "False" or "false" or "0" ? "literalFalse" : null;
        if (node.Kind == "Invocation")
        {
            if (node.Name == "IsDeadAccount")
            {
                RequireDeadAccountPredicate(node);
                return "recipientDead";
            }
            if (node.Name == "ParticipatesInNormalBlockCounters")
            {
                RequireNormalCounterPredicate(node);
                return "normalCounters";
            }
        }
        string? primitive = node.Name switch
        {
            "IsEip8037Enabled" => "eip8037",
            "IsEip7708Enabled" => "eip7708",
            "IsTracingActions" or "isTracingActions" => "tracingActions",
            "IsTracingCode" or "isTracingCode" => "tracingCode",
            "IsTracingLogs" or "isTracingLogs" => "tracingLogs",
            "IsTracingAccess" or "isTracingAccess" => "tracingAccess",
            "IsTracingFees" or "isTracingFees" => "tracingFees",
            "IsTracingReceipt" or "isTracingReceipt" => "tracingReceipt",
            "IsTracingState" or "isTracingState" => "tracingState",
            "UseHotAndColdStorage" => "useHotStorage",
            "UseTxAccessLists" => "useTxAccessLists",
            "AddCoinbaseToTxAccessList" => "addCoinbase",
            "warmUpRecipient" => "warmRecipient",
            "senderIsRecipient" => "selfSend",
            "hasValueTransfer" => "hasValue",
            "newAccountOutOfGas" => "outOfGas",
            "restore" => "restore",
            "commit" => "commit",
            "true" or "True" => "literalTrue",
            "false" or "False" => "literalFalse",
            "BuildUp" => "buildUp",
            _ => null,
        };
        if (primitive is not null) RequirePredicateReference(node, primitive);
        return primitive;
    }

    private static void RequirePredicateReference(TypedAstNode node, string primitive)
    {
        if (primitive == "tracingActions" && node.Kind == "LocalReference" && node.Name == "isTracingActions")
        {
            if (node.Children.Length != 0 || !IsBoolean(node))
                throw new ExtractionException("The action-tracing local lost its exact typed boolean grammar.");
            return;
        }

        bool property = primitive is "eip8037" or "eip7708" or "tracingActions" or "tracingCode" or
            "tracingLogs" or "tracingAccess" or "tracingFees" or "tracingReceipt" or "tracingState" or
            "useHotStorage" or "useTxAccessLists" or "addCoinbase";
        bool literal = primitive is "literalTrue" or "literalFalse" or "buildUp";
        if (literal) return;
        if (property)
        {
            if (node.Kind != "PropertyReference" || node.Children.Length != 1 || !IsBoolean(node) ||
                node.Children[0].Kind != "ParameterReference" || node.Children[0].Name is not ("spec" or "tracer"))
                throw new ExtractionException($"Predicate primitive {primitive} lost its exact typed receiver/property grammar.");
            string expectedReceiver = primitive is "tracingActions" or "tracingCode" or "tracingLogs" or
                "tracingAccess" or "tracingFees" or "tracingReceipt" or "tracingState" ? "tracer" : "spec";
            if (node.Children[0].Name != expectedReceiver)
                throw new ExtractionException($"Predicate primitive {primitive} changed its typed receiver to {node.Children[0].Name}.");
            return;
        }

        if (primitive is "selfSend" or "hasValue" or "outOfGas" or "restore" or "commit" or "warmRecipient")
        {
            if (node.Kind is not ("LocalReference" or "ParameterReference") || node.Children.Length != 0 || !IsBoolean(node))
                throw new ExtractionException($"Predicate primitive {primitive} lost its exact typed boolean reference.");
            return;
        }

        if (primitive is "recipientDead" or "normalCounters") return;
        throw new ExtractionException($"Predicate primitive {primitive} is not closed over an admitted typed grammar.");
    }

    private static void RequireDeadAccountPredicate(TypedAstNode node)
    {
        if (!IsBoolean(node) || node.Children.Length != 2 ||
            node.Children[0].Kind != "PropertyReference" || node.Children[0].Name != "WorldState" ||
            node.Children[0].Children.Length != 1 || node.Children[0].Children[0].Kind != "InstanceReference" ||
            node.Children[1].Kind != "Argument" || node.Children[1].Name != "address" ||
            node.Children[1].ParameterName != "address" || node.Children[1].Children.Length != 1 ||
            node.Children[1].Children[0].Kind != "ParameterReference" || node.Children[1].Children[0].Name != "recipient")
            throw new ExtractionException("IsDeadAccount predicate changed its exact WorldState/recipient argument grammar.");
    }

    private static void RequireNormalCounterPredicate(TypedAstNode node)
    {
        if (!IsBoolean(node) || node.Children.Length != 2 || node.Children.Any(static child => child.Kind != "Argument") ||
            node.Children[0].Name != "options" || node.Children[1].Name != "parallel" ||
            node.Children[0].ParameterName != "options" || node.Children[1].ParameterName != "parallel" ||
            node.Children[0].Children.Length != 1 || node.Children[1].Children.Length != 1 ||
            node.Children[0].Children[0].Kind != "ParameterReference" || node.Children[0].Children[0].Name != "opts" ||
            node.Children[1].Children[0].Kind != "FieldReference" || node.Children[1].Children[0].Name != "_parallel")
            throw new ExtractionException("ParticipatesInNormalBlockCounters predicate changed its exact options/parallel grammar.");
    }

    private static bool IsBoolean(TypedAstNode node) => node.Type is "bool" or "Boolean";

    private static bool IsBuildUpComparison(TypedAstNode node)
    {
        node = UnwrapPredicate(node);
        return node.Kind == "Binary" && (node.Name is "Equals" or "Equal") && node.Children.Length == 2 &&
            ((IsOptionParameter(node.Children[0], "opts") && IsExecutionOptionField(node.Children[1], "BuildUp")) ||
             (IsExecutionOptionField(node.Children[0], "BuildUp") && IsOptionParameter(node.Children[1], "opts")));
    }

    private static TypedAstNode UnwrapPredicate(TypedAstNode node)
    {
        while (node.Kind is "Parenthesized" or "Conversion" && node.Children.Length == 1) node = node.Children[0];
        return node;
    }

    private static bool IsOptionParameter(TypedAstNode node, string name) =>
        node.Kind == "ParameterReference" && node.Name == name && node.Children.Length == 0 &&
        node.Type.Contains("ExecutionOptions", StringComparison.Ordinal);

    private static bool IsExecutionOptionField(TypedAstNode node, string name) =>
        node.Kind == "FieldReference" && node.Name == name && node.Children.Length == 0 &&
        node.Type.Contains("ExecutionOptions", StringComparison.Ordinal);

    private static string EmitOperationSet(IrDocument document) =>
        "[" + string.Join(", ", document.Completion.Operations.Select(operation =>
            "{ operation := ." + OperationConstructor(operation.Id) + ", formula := " + LeanString(operation.Lowering.Formula) +
            ", targetIdentity := " + LeanString(operation.Lowering.TargetSymbolIdentity) +
            ", grammar := " + LeanString(operation.Lowering.Grammar) +
            ", executionTerm := " + LeanString(operation.Lowering.ExecutionTerm) + " }")) + "]";

    private static string OperationConstructor(string id) => id switch
    {
        "incrementEmptyCalls" => "incrementEmptyCalls",
        "newAccountStateCost" => "newAccountStateCost",
        "consumeStateGas" => "consumeStateGas",
        "payValueRequest" => "payValueRequest",
        "addRecipientRequest" => "addRecipientRequest",
        "actionStartRequest" => "actionStartRequest",
        "clearExecutionGas" => "clearExecutionGas",
        "transferLogRequest" => "transferLogRequest",
        "substateConstruction" => "substateConstruction",
        "refundRequest" => "refundRequest",
        "accessRequest" => "accessRequest",
        "headerAndFees" => "headerAndFees",
        "finalizeRequest" => "finalizeRequest",
        "receiptFailure" => "receiptFailure",
        "receiptSuccess" => "receiptSuccess",
        _ => throw new ExtractionException($"No structural operation constructor exists for {id}."),
    };

    private static string ExecutionKind(OperationShape operation)
    {
        int separator = operation.Lowering.ExecutionTerm.IndexOf('|');
        string kind = separator < 0 ? operation.Lowering.ExecutionTerm : operation.Lowering.ExecutionTerm[..separator];
        if (string.IsNullOrWhiteSpace(kind))
            throw new ExtractionException($"Source operation {operation.Id} has no closed executable-term kind.");
        return kind;
    }

    private static string OperationFunction(IrDocument document, string id)
    {
        OperationShape operation = document.Completion.Operations.Single(candidate => candidate.Id == id);
        string kind = ExecutionKind(operation);
        string expected = id switch
        {
            "incrementEmptyCalls" => "metric",
            "newAccountStateCost" => "newAccountStateCost",
            "consumeStateGas" => "stateCharge",
            "payValueRequest" => "payValue",
            "addRecipientRequest" => "recipientWrite",
            "actionStartRequest" => "actionStart",
            "clearExecutionGas" => "clearGas",
            "transferLogRequest" => "transferLog",
            "substateConstruction" => "substate",
            "refundRequest" => "settlement",
            "accessRequest" => "access",
            "headerAndFees" => "headerFees",
            "finalizeRequest" => "finalize",
            "receiptFailure" => "receiptFailure",
            "receiptSuccess" => "receiptSuccess",
            _ => throw new ExtractionException($"No executable operation function exists for {id}.")
        };
        if (!string.Equals(kind, expected, StringComparison.Ordinal))
            throw new ExtractionException($"Operation {id} lowered to {kind}, expected {expected}.");
        return "sourceOp_" + OperationConstructor(id);
    }

    /// <summary>
    /// Emits the operation bodies selected by the exact typed source invocation terms.
    /// </summary>
    /// <remarks>
    /// The transition composer below only sequences these generated operation terms. The operation
    /// bodies are not selected from source-text labels or from a hard-coded semantic flag; each one
    /// is available only after <see cref="Extractor.LowerTypedOperation"/> has rebound its receiver,
    /// overload, ordered arguments, child expressions, and target identity.
    /// </remarks>
    private static string EmitOperationDefinitions(IrDocument document)
    {
        string Definition(string id, string body)
        {
            _ = OperationFunction(document, id);
            return body;
        }

        List<string> definitions = [];
        foreach (OperationShape operation in document.Completion.Operations.OrderBy(static operation => operation.Ordinal))
        {
            string kind = ExecutionKind(operation);
            string function = "sourceOp_" + OperationConstructor(operation.Id);
            string definition = kind switch
            {
                "metric" => Definition(operation.Id, "def " + function + " : List Effect := [.metricEmptyCalls]"),
                "newAccountStateCost" => Definition(operation.Id, "def " + function + " : Nat := sourceNewAccountStateCost"),
                "stateCharge" => Definition(operation.Id, "def " + function + " (input : CompletionInput) : StateChargeResponse :=\n" +
                    "  stateChargeFromKernelWithCost input (" + OperationFunction(document, "newAccountStateCost") + " : Int)"),
                "payValue" => Definition(operation.Id, "def " + function + " (input : CompletionInput) (write : Bool) : List Effect :=\n" +
                    "  if write && input.handoff.tx.value != 0 then [.world (.subtract input.handoff.tx.sender (payValueAmount input) input.handoff.spec)] else []"),
                "recipientWrite" => Definition(operation.Id, "def " + function + " (input : CompletionInput) (write : Bool) : List Effect :=\n" +
                    "  if write then [.world (.addOrCreate input.handoff.recipient input.handoff.tx.value input.handoff.spec)] else []"),
                "actionStart" => Definition(operation.Id, "def " + function + " (input : CompletionInput) (selfSend outOfGas : Bool) (gasAfterCharge : GasPolicy) : List Effect :=\n" +
                    "  if evalSourcePredicate sourceActionsPredicate input selfSend outOfGas then\n" +
                    "    [.action (.actionStart (gasRemaining gasAfterCharge) input.handoff.tx.value input.handoff.tx.sender input.handoff.recipient input.handoff.tx.data .transaction false)] ++\n" +
                    "      (if input.handoff.tracer.isTracingCode then [.action (.byteCode [])] else [])\n" +
                    "  else []"),
                "clearGas" => Definition(operation.Id, "def " + function + " (gas : GasPolicy) : GasPolicy := clearExecutionGas gas"),
                "transferLog" => Definition(operation.Id, "def " + function + " (input : CompletionInput) : TransferLog :=\n" +
                    "  { address := { id := sourceTransferLogAddress }\n" +
                    "    topics := [sourceTransferSignature, addressHashProjection input.handoff.tx.sender, addressHashProjection input.handoff.recipient]\n" +
                    "    data := transferDataFor input.handoff.tx.value\n" +
                    "    fromAddress := input.handoff.tx.sender\n" +
                    "    toAddress := input.handoff.recipient\n" +
                    "    amount := input.handoff.tx.value }"),
                "substate" => Definition(operation.Id, "def " + function + " (logs : List TransferLog) (outOfGas : Bool) : Substate :=\n" +
                    "  { output := []\n" +
                    "    refund := 0\n" +
                    "    logs\n" +
                    "    shouldRevert := false\n" +
                    "    isError := false\n" +
                    "    error := none\n" +
                    "    exception := if outOfGas then EvmException.outOfGas else EvmException.none }"),
                "settlement" => Definition(operation.Id, "def " + function + " (input : SettlementInput) : GasConsumed :=\n" +
                    "  let result := settlement input\n" +
                    "  { spentGas := result.spentGas\n" +
                    "    operationGas := result.operationGas\n" +
                    "    blockGas := result.blockGas\n" +
                    "    blockStateGas := result.blockStateGas\n" +
                    "    maxUsedGas := result.maxUsedGas\n" +
                    "    gasRefund := result.gasRefund }"),
                "access" => Definition(operation.Id, "def " + function + " (input : CompletionInput) : AccessObservation := accessObservation input"),
                "headerFees" => Definition(operation.Id, "def " + function + " (input : CompletionInput) (selfSend outOfGas : Bool) (effectiveBlock counterGas : Nat) : Header :=\n" +
                    "  if evalSourcePredicate sourceCountersPredicate input selfSend outOfGas then\n" +
                    "    { input.handoff.header with gasUsed := if input.handoff.spec.eip8037Enabled then counterGas else input.handoff.header.gasUsed + effectiveBlock }\n" +
                    "  else input.handoff.header\n\n" +
                    "def sourceOp_headerAndFeesEffects (input : CompletionInput) (substate : Substate) (spent : GasConsumed) (statusFailure : Bool) : List Effect :=\n" +
                    "  feeEffects input substate spent statusFailure"),
                "finalize" => Definition(operation.Id, "def " + function + " (input : CompletionInput) (effectiveBlock spent : Nat) : Transaction :=\n" +
                    "  if isWarmup input.handoff.options then input.handoff.tx\n" +
                    "  else { input.handoff.tx with blockGasUsed := effectiveBlock, spentGas := spent }\n\n" +
                    "def sourceOp_finalizeRequestWorld (input : CompletionInput) (selfSend outOfGas : Bool) : List Effect :=\n" +
                    "  if evalSourcePredicate sourceRestorePredicate input selfSend outOfGas then\n" +
                    "    [.world (.reset false)] ++\n" +
                    "      (if input.handoff.deleteCallerAccount then [.world (.deleteAccount input.handoff.tx.sender)] else\n" +
                    "        (if input.handoff.senderReservedGasPayment != 0 then [.world (.add input.handoff.tx.sender input.handoff.senderReservedGasPayment input.handoff.spec)] else []) ++\n" +
                    "        [.world (.decrementNonce input.handoff.tx.sender), .world (.commit input.handoff.spec false false)])\n" +
                    "  else if evalSourcePredicate sourceCommitPredicate input selfSend outOfGas then\n" +
                    "    [.world (.commit input.handoff.spec (!input.handoff.spec.eip658Enabled) input.handoff.tracer.isTracingState)]\n" +
                    "  else\n" +
                    "    [.world .resetTransient] ++ (if buildUpApplies input && input.handoff.spec.eip8037Enabled then [.world .reapEmptyAccounts] else [])"),
                "receiptFailure" => Definition(operation.Id, "def " + function + " (receipt : ReceiptContinuationInput) : Effect :=\n" +
                    "  .receipt (.receiptFailed receipt.executingAccount receipt.spentGas.spentGas receipt.output receipt.error receipt.stateRoot)"),
                "receiptSuccess" => Definition(operation.Id, "def " + function + " (receipt : ReceiptContinuationInput) : Effect :=\n" +
                    "  .receipt (.receiptSuccess receipt.executingAccount receipt.spentGas.spentGas receipt.output receipt.logs receipt.stateRoot)"),
                _ => throw new ExtractionException($"No structural executable definition exists for source term {kind}.")
            };
            definitions.Add(definition);
        }
        return string.Join('\n', definitions);
    }

    /// <summary>Builds the executable phase composition from the admitted operation keys.</summary>
    /// <remarks>
    /// The prelude contains only value types and small adapter helpers. Every effectful phase below
    /// calls a bounded model operation definition selected from the exact grammar rebound by Roslyn;
    /// a missing or changed typed invocation is rejected before this method is reached. The selected
    /// grammar is an admission guard, not a production-body interpreter.
    /// </remarks>
    private static string EmitRun(IrDocument document)
    {
        string Function(string id) => OperationFunction(document, id);
        StringBuilder run = new();
        run.AppendLine("def settlementInputOf (input : CompletionInput) (substate : Substate) (preRefund : Nat) (stateUsed : Int) : SettlementInput :=");
        run.AppendLine("  { gasLimit := input.handoff.tx.gasLimit");
        run.AppendLine("    preRefundGas := preRefund");
        run.AppendLine("    refundCounter := substate.refund");
        run.AppendLine("    destroyCount := 0");
        run.AppendLine("    destroyRefund := input.handoff.spec.destroyRefund");
        run.AppendLine("    codeInsertExecutionRefund := 0");
        run.AppendLine("    calldataFloorGas := input.handoff.intrinsic.floorGas");
        run.AppendLine("    stateGasUsed := stateUsed");
        run.AppendLine("    refundQuotient := input.handoff.spec.refundQuotient");
        run.AppendLine("    isError := substate.isError");
        run.AppendLine("    shouldRevert := substate.shouldRevert");
        run.AppendLine("    isEip8037Enabled := input.handoff.spec.eip8037Enabled");
        run.AppendLine("    isEip7778Enabled := input.handoff.spec.eip7778Enabled }");
        run.AppendLine("def run (input : CompletionInput) : SimpleComplete :=");
        run.AppendLine("  let handoff := input.handoff");
        run.AppendLine("  let selfSend := sameAddress handoff.tx.sender handoff.recipient");
        run.AppendLine("  let computedCharge := " + Function("consumeStateGas") + " input");
        run.AppendLine("  let charged := if chargeApplies input selfSend then computedCharge else { succeeded := true, gas := handoff.gasAvailable }");
        run.AppendLine("  let newAccountOutOfGas := !charged.succeeded");
        run.AppendLine("  let gasAfterCharge := charged.gas");
        run.AppendLine("  let write := writeApplies input selfSend newAccountOutOfGas");
        run.AppendLine("  let transferLog := " + Function("transferLogRequest") + " input");
        run.AppendLine("  let logs := if logApplies input selfSend newAccountOutOfGas then [transferLog] else []");
        run.AppendLine("  let actionStart := " + Function("actionStartRequest") + " input selfSend false gasAfterCharge");
        run.AppendLine("  let gasAfterOog := if newAccountOutOfGas then " + Function("clearExecutionGas") + " gasAfterCharge else gasAfterCharge");
        run.AppendLine("  let clearEffect := if newAccountOutOfGas then [.clearExecutionGas gasAfterCharge gasAfterOog] else []");
        run.AppendLine("  let logEffect := if logApplies input selfSend newAccountOutOfGas then [.logCreated transferLog] else []");
        run.AppendLine("  let logTrace := if logApplies input selfSend newAccountOutOfGas && handoff.tracer.isTracingLogs then [.action (.log transferLog)] else []");
        run.AppendLine("  let substate := " + Function("substateConstruction") + " logs newAccountOutOfGas");
        run.AppendLine("  let actionEnd :=");
        run.AppendLine("    if evalSourcePredicate sourceActionsPredicate input selfSend newAccountOutOfGas then");
        run.AppendLine("      if newAccountOutOfGas then [.action (.actionError EvmException.outOfGas)] else [.action (.actionEnd (gasRemaining gasAfterOog) [])]");
        run.AppendLine("    else []");
        run.AppendLine("  let stateUsed := if handoff.spec.eip8037Enabled then stateGasUsed gasAfterOog else 0");
        run.AppendLine("  let computedPreRefundGas := preRefundGas handoff.tx.gasLimit gasAfterOog");
        run.AppendLine("  let settlementInput := settlementInputOf input substate computedPreRefundGas stateUsed");
        run.AppendLine("  let settled := " + Function("refundRequest") + " settlementInput");
        run.AppendLine("  let spent := { spentGas := settled.spentGas, operationGas := settled.operationGas, blockGas := settled.blockGas, blockStateGas := settled.blockStateGas, maxUsedGas := settled.maxUsedGas, gasRefund := settled.gasRefund }");
        run.AppendLine("  let refundAmount := (handoff.tx.gasLimit - spent.spentGas) * handoff.opcodeGasPrice");
        run.AppendLine("  let shouldValidateGas := !isSkipValidation handoff.options || handoff.tx.maxFeePerGas != 0 || handoff.tx.maxPriorityFeePerGas != 0");
        run.AppendLine("  let refundEffect := if handoff.opcodeGasPrice != 0 && shouldValidateGas && refundAmount != 0 then [.world (.add handoff.tx.sender refundAmount handoff.spec)] else []");
        run.AppendLine("  let access := " + Function("accessRequest") + " input");
        run.AppendLine("  let accessEffect := if handoff.tracer.isTracingAccess then [.action (.access access.addresses access.storageCells)] else []");
        run.AppendLine("  let effectiveBlock := effectiveBlockGas spent");
        run.AppendLine("  let cumulativeExecutionGas := if handoff.spec.eip8037Enabled then handoff.blockCumulativeExecutionGas + effectiveBlock else handoff.blockCumulativeExecutionGas");
        run.AppendLine("  let cumulativeStateGas := if handoff.spec.eip8037Enabled then handoff.blockCumulativeStateGas + settled.blockStateGas else handoff.blockCumulativeStateGas");
        run.AppendLine("  let counterGas := if handoff.spec.eip8037Enabled then combineBlockGas cumulativeExecutionGas cumulativeStateGas else effectiveBlock");
        run.AppendLine("  let header := " + Function("headerAndFees") + " input selfSend newAccountOutOfGas effectiveBlock counterGas");
        run.AppendLine("  let statusFailure := newAccountOutOfGas");
        run.AppendLine("  let feeEffect := " + Function("headerAndFees") + "Effects input substate spent statusFailure");
        run.AppendLine("  let txAfterFields := " + Function("finalizeRequest") + " input effectiveBlock spent.spentGas");
        run.AppendLine("  let finalWorld := " + Function("finalizeRequest") + "World input selfSend newAccountOutOfGas");
        run.AppendLine("  let rootEffect := if evalSourcePredicate sourceReceiptPredicate input selfSend newAccountOutOfGas && !handoff.spec.eip658Enabled then [.world (.recalculateStateRoot input.world.stateRootBeforeRecalculate input.world.stateRootAfterRecalculate)] else []");
        run.AppendLine("  let receipt := receiptInput input substate spent statusFailure");
        run.AppendLine("  let receiptEffect :=");
        run.AppendLine("    if evalSourcePredicate sourceReceiptPredicate input selfSend newAccountOutOfGas then");
        run.AppendLine("      if statusFailure then [" + Function("receiptFailure") + " receipt] else [" + Function("receiptSuccess") + " receipt]");
        run.AppendLine("    else []");
        run.AppendLine("  let result := if newAccountOutOfGas then TransactionResult.evmException EvmException.outOfGas else TransactionResult.ok");
        run.AppendLine("  let postCumulativeExecutionGas := if evalSourcePredicate sourceCountersPredicate input selfSend newAccountOutOfGas then cumulativeExecutionGas else handoff.blockCumulativeExecutionGas");
        run.AppendLine("  let postCumulativeStateGas := if evalSourcePredicate sourceCountersPredicate input selfSend newAccountOutOfGas then cumulativeStateGas else handoff.blockCumulativeStateGas");
        run.AppendLine("  { tx := txAfterFields");
        run.AppendLine("    header");
        run.AppendLine("    substate");
        run.AppendLine("    spentGas := spent");
        run.AppendLine("    preRefundGas := computedPreRefundGas");
        run.AppendLine("    result");
        run.AppendLine("    blockCumulativeExecutionGas := postCumulativeExecutionGas");
        run.AppendLine("    blockCumulativeStateGas := postCumulativeStateGas");
        run.AppendLine("    receiptContinuation := receipt");
        run.AppendLine("    effects := " + Function("incrementEmptyCalls") + " ++");
        run.AppendLine("      (if chargeApplies input selfSend then [.gasStateCharge charged] else []) ++");
        run.AppendLine("      " + Function("payValueRequest") + " input write ++ " + Function("addRecipientRequest") + " input write ++");
        run.AppendLine("      actionStart ++ clearEffect ++");
        run.AppendLine("      logEffect ++ logTrace ++ [.substateBuilt substate] ++ actionEnd ++");
        run.AppendLine("      [.settlement spent] ++ refundEffect ++ accessEffect ++");
        run.AppendLine("      (if evalSourcePredicate sourceCountersPredicate input selfSend newAccountOutOfGas then [.headerGasUsed header.gasUsed] else []) ++ feeEffect ++");
        run.AppendLine("      (if !isWarmup handoff.options then [.transactionFields effectiveBlock spent.spentGas] else []) ++");
        run.AppendLine("      finalWorld ++ rootEffect ++ receiptEffect }");
        return run.ToString().TrimEnd('\r', '\n');
    }

    private static string QuoteList(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(LeanString)) + "]";

    private static string LeanString(string value)
    {
        StringBuilder builder = new("\"");
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(character); break;
            }
        }
        return builder.Append('"').ToString();
    }

    private const string Template = """
-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only
-- Generated by SimpleTransferCompletionExtractor. Do not edit.

import Lean
import Eip803x.Generated.StateGasChargeKernel
import Eip803x.Generated.TransactionSettlementKernel
import Eip803x.Generated.SystemTransactionRoutingKernel

namespace SimpleTransferCompletionExtractor.Generated

def sourceIrSha256 : String := "__IR_SHA256__"
def sourceAcceptanceState : String := __ACCEPTANCE_STATE__
def sourceClosureSha256 : String := "__SOURCE_CLOSURE__"
def handoffArtifactSha256 : String := "__HANDOFF_SHA__"
def settlementArtifactSha256 : String := "__SETTLEMENT_SHA__"
def stateChargeArtifactSha256 : String := "__STATE_CHARGE_SHA__"
def routingArtifactSha256 : String := "__ROUTING_SHA__"
def sourceStages : List String := __STAGES__
def sourceBranches : List String := __BRANCHES__
def sourceEffects : List String := __EFFECTS__
def sourceOperations : List String := __OPERATIONS__
inductive SourceOperation where
  | incrementEmptyCalls
  | newAccountStateCost
  | consumeStateGas
  | payValueRequest
  | addRecipientRequest
  | actionStartRequest
  | clearExecutionGas
  | transferLogRequest
  | substateConstruction
  | refundRequest
  | accessRequest
  | headerAndFees
  | finalizeRequest
  | receiptFailure
  | receiptSuccess
  deriving DecidableEq, Repr
structure SourceOperationSpec where
  operation : SourceOperation
  formula : String
  targetIdentity : String
  grammar : String
  executionTerm : String
  deriving DecidableEq, Repr
def sourceOperationSpecs : List SourceOperationSpec := __OPERATION_SET__
def sourceOperationAvailable (operation : SourceOperation) : Bool :=
  (sourceOperationSpecs.any (fun specification =>
    specification.operation == operation && specification.formula != "" &&
      specification.targetIdentity != "" && specification.grammar != "" && specification.executionTerm != ""))
def sourceAdapters : List String := __ADAPTERS__
def sourceBindings : List String := __BINDINGS__
def sourceFormulaModes : List String := __FORMULAS__

-- These finite source facts are retained as audit metadata. The emitted transition is a bounded
-- model; the source admission does not establish the production body or adapter semantics.
def sourceNewAccountStateCost : Nat := __NEW_ACCOUNT_COST__
def sourceTransferLogAddress : Nat := __TRANSFER_LOG_ADDRESS__
def sourceTransferSignature : Nat := __TRANSFER_SIGNATURE__
def sourceTransferLogPayloadGrammar : String := __TRANSFER_LOG_PAYLOAD__
def transferDataFor (value : Nat) : List Nat :=
  (List.range 32).reverse.map (fun index => (value / (256 ^ index)) % 256)
def sourceNoFrameGotoCount : Nat := __NO_FRAME_GOTO_COUNT__
def sourceNoFrameCfgProven : Bool := __NO_FRAME_CFG_PROVEN__
def sourceFailContractCreateBypassesNoFrame : Bool := __FAIL_CREATE_BYPASS__
def sourceNoFrameCfgPaths : List String := __NO_FRAME_CFG_PATHS__

structure Address where
  id : Nat
  deriving DecidableEq, Repr

-- Bounded abstraction of Address.ToHash().ToHash256(); this package does not prove Keccak
-- correctness or the production hash implementation.
def addressHashProjection (address : Address) : Nat := address.id

def sameAddress (left right : Address) : Bool := left.id == right.id

structure StorageCell where
  address : Address
  key : Nat
  deriving DecidableEq, Repr

structure AccessEntry where
  address : Address
  storageKeys : List Nat
  deriving DecidableEq, Repr

structure AccessObservation where
  addresses : List Address
  storageCells : List StorageCell
  deriving DecidableEq, Repr

structure Transaction where
  sender : Address
  recipient : Address
  value : Nat
  data : List Nat
  accessList : List AccessEntry
  gasLimit : Nat
  isFree : Bool
  supportsBlobs : Bool
  maxFeePerGas : Nat
  maxPriorityFeePerGas : Nat
  blockGasUsed : Nat
  spentGas : Nat
  deriving DecidableEq, Repr

structure Header where
  gasUsed : Nat
  baseFeePerGas : Nat
  gasBeneficiary : Address
  stateRoot : Option Nat
  deriving DecidableEq, Repr

structure Spec where
  eip8037Enabled : Bool
  eip7708Enabled : Bool
  eip658Enabled : Bool
  eip3529Enabled : Bool
  eip7778Enabled : Bool
  eip1559Enabled : Bool
  eip4844FeeCollectorEnabled : Bool
  useHotAndColdStorage : Bool
  useTxAccessLists : Bool
  addCoinbaseToTxAccessList : Bool
  feeCollector : Option Address
  destroyRefund : Nat
  refundQuotient : Nat
  deriving DecidableEq, Repr

structure Options where
  raw : Nat
  deriving DecidableEq, Repr

def executionOptionCommit : Nat := 1
def executionOptionRestore : Nat := 2
def executionOptionSkipValidation : Nat := 4
def executionOptionWarmup : Nat := 8
def executionOptionBuildUp : Nat := 16

def hasFlag (options flag : Nat) : Bool := flag != 0 && ((options / flag) % 2 == 1)
def isRestore (options : Options) : Bool := hasFlag options.raw executionOptionRestore
def isCommit (options : Options) : Bool := hasFlag options.raw executionOptionCommit
def isSkipValidation (options : Options) : Bool := hasFlag options.raw executionOptionSkipValidation
def isWarmup (options : Options) : Bool := hasFlag options.raw executionOptionWarmup
def isBuildUp (options : Options) : Bool := options.raw == executionOptionBuildUp

structure Tracer where
  isTracingActions : Bool
  isTracingCode : Bool
  isTracingLogs : Bool
  isTracingAccess : Bool
  isTracingFees : Bool
  isTracingReceipt : Bool
  isTracingState : Bool
  deriving DecidableEq, Repr

structure Intrinsic where
  floorGas : Nat
  standardGas : Nat
  deriving DecidableEq, Repr

structure GasPolicy where
  execution : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

structure StateChargeResponse where
  succeeded : Bool
  gas : GasPolicy
  deriving DecidableEq, Repr

structure GasAdapter where
  newAccountStateCost : Nat
  stateCharge : StateChargeResponse
  preRefundGas : Nat
  deriving DecidableEq, Repr

inductive WorldRequest where
  | subtract (address : Address) (value : Nat) (spec : Spec)
  | add (address : Address) (value : Nat) (spec : Spec)
  | addOrCreate (address : Address) (value : Nat) (spec : Spec)
  | reset (resetBlockChanges : Bool)
  | deleteAccount (address : Address)
  | decrementNonce (address : Address)
  | commit (spec : Spec) (commitRoots : Bool) (stateTracing : Bool)
  | resetTransient
  | reapEmptyAccounts
  | recalculateStateRoot (before after : Option Nat)
  deriving DecidableEq, Repr

-- World/root fields are opaque normal-return request-boundary inputs. Production mutation and
-- state-root implementations require a separate adapter theorem.
structure WorldAdapter where
  recipientIsDead : Bool
  senderBalance : Nat
  stateRootBeforeRecalculate : Option Nat
  stateRootAfterRecalculate : Option Nat
  deriving DecidableEq, Repr

structure TransferLog where
  address : Address
  topics : List Nat
  data : List Nat
  fromAddress : Address
  toAddress : Address
  amount : Nat
  deriving DecidableEq, Repr

inductive EvmException where
  | none
  | outOfGas
  deriving DecidableEq, Repr

inductive ActionKind where
  | transaction
  deriving DecidableEq, Repr

inductive TraceRequest where
  | actionStart (gas value : Nat) (fromAddress toAddress : Address) (data : List Nat) (kind : ActionKind) (isPrecompileCall : Bool)
  | byteCode (code : List Nat)
  | actionError (exception : EvmException)
  | actionEnd (gas : Nat) (output : List Nat)
  | log (log : TransferLog)
  | access (addresses : List Address) (storageCells : List StorageCell)
  | fees (fees burnt : Nat)
  | receiptFailed (account : Address) (spent : Nat) (output : List Nat) (error : Option String) (root : Option Nat)
  | receiptSuccess (account : Address) (spent : Nat) (output : List Nat) (logs : List TransferLog) (root : Option Nat)
  deriving DecidableEq, Repr

-- Trace fields are normal-return request-boundary inputs; callback effects/exceptions are outside
-- this model. ReportAccess is compared extensionally by the independent reference relation.
structure TraceAdapter where
  normalReturn : Bool
  deriving DecidableEq, Repr

structure SettlementInput where
  gasLimit : Nat
  preRefundGas : Nat
  refundCounter : Int
  destroyCount : Int
  destroyRefund : Nat
  codeInsertExecutionRefund : Nat
  calldataFloorGas : Nat
  stateGasUsed : Int
  refundQuotient : Nat
  isError : Bool
  shouldRevert : Bool
  isEip8037Enabled : Bool
  isEip7778Enabled : Bool
  deriving DecidableEq, Repr

def settlement (input : SettlementInput) : Eip803x.Generated.TransactionSettlementKernel.TransactionSettlementResult :=
  Eip803x.Generated.TransactionSettlementKernel.calculate
    input.gasLimit input.preRefundGas input.refundCounter input.destroyCount input.destroyRefund
    input.codeInsertExecutionRefund input.calldataFloorGas input.stateGasUsed input.refundQuotient
    input.isError input.shouldRevert input.isEip8037Enabled input.isEip7778Enabled

structure Handoff where
  tx : Transaction
  header : Header
  spec : Spec
  tracer : Tracer
  options : Options
  restore : Bool
  commit : Bool
  deleteCallerAccount : Bool
  recipient : Address
  intrinsic : Intrinsic
  gasAvailable : GasPolicy
  opcodeGasPrice : Nat
  premiumPerGas : Nat
  senderReservedGasPayment : Nat
  blobBaseFee : Nat
  blockCumulativeExecutionGas : Nat
  blockCumulativeStateGas : Nat
  parallel : Bool
  deriving DecidableEq, Repr

structure CompletionInput where
  handoff : Handoff
  gas : GasAdapter
  world : WorldAdapter
  tracer : TraceAdapter
  deriving DecidableEq, Repr

-- The executable branch terms below are structural lowerings of the resolved Roslyn condition
-- trees. They are interpreted against the completion input; source metadata is not consulted by
-- the transition itself.
inductive SourcePredicate where
  | literalTrue
  | literalFalse
  | eip8037
  | eip7708
  | tracingActions
  | tracingCode
  | tracingLogs
  | tracingAccess
  | tracingFees
  | tracingReceipt
  | tracingState
  | useHotStorage
  | useTxAccessLists
  | addCoinbase
  | warmRecipient
  | selfSend
  | hasValue
  | outOfGas
  | recipientDead
  | restore
  | commit
  | buildUp
  | normalCounters
  | all (left right : SourcePredicate)
  | any (left right : SourcePredicate)
  | negate (value : SourcePredicate)
  deriving DecidableEq, Repr

def evalSourcePredicate (predicate : SourcePredicate) (input : CompletionInput) (selfSend outOfGas : Bool) : Bool :=
  match predicate with
  | .literalTrue => true
  | .literalFalse => false
  | .eip8037 => input.handoff.spec.eip8037Enabled
  | .eip7708 => input.handoff.spec.eip7708Enabled
  | .tracingActions => input.handoff.tracer.isTracingActions
  | .tracingCode => input.handoff.tracer.isTracingCode
  | .tracingLogs => input.handoff.tracer.isTracingLogs
  | .tracingAccess => input.handoff.tracer.isTracingAccess
  | .tracingFees => input.handoff.tracer.isTracingFees
  | .tracingReceipt => input.handoff.tracer.isTracingReceipt
  | .tracingState => input.handoff.tracer.isTracingState
  | .useHotStorage => input.handoff.spec.useHotAndColdStorage
  | .useTxAccessLists => input.handoff.spec.useTxAccessLists
  | .addCoinbase => input.handoff.spec.addCoinbaseToTxAccessList
  | .warmRecipient => true
  | .selfSend => selfSend
  | .hasValue => input.handoff.tx.value != 0
  | .outOfGas => outOfGas
  | .recipientDead => input.world.recipientIsDead
  | .restore => input.handoff.restore
  | .commit => input.handoff.commit
  | .buildUp => input.handoff.options.raw == executionOptionBuildUp
  | .normalCounters =>
      Eip803x.Generated.SystemTransactionRoutingKernel.participatesInNormalBlockCounters
        { commit := isCommit input.handoff.options
          restore := isRestore input.handoff.options
          skipValidation := isSkipValidation input.handoff.options
          warmup := isWarmup input.handoff.options
          buildUp := isBuildUp input.handoff.options }
        input.handoff.parallel
  | .all left right => evalSourcePredicate left input selfSend outOfGas && evalSourcePredicate right input selfSend outOfGas
  | .any left right => evalSourcePredicate left input selfSend outOfGas || evalSourcePredicate right input selfSend outOfGas
  | .negate value => !(evalSourcePredicate value input selfSend outOfGas)

__PREDICATES__

structure Substate where
  output : List Nat
  refund : Int
  logs : List TransferLog
  shouldRevert : Bool
  isError : Bool
  error : Option String
  exception : EvmException
  deriving DecidableEq, Repr

structure GasConsumed where
  spentGas : Nat
  operationGas : Nat
  blockGas : Nat
  blockStateGas : Nat
  maxUsedGas : Nat
  gasRefund : Nat
  deriving DecidableEq, Repr

inductive TransactionResult where
  | ok
  | evmException (exception : EvmException)
  deriving DecidableEq, Repr

structure ReceiptContinuationInput where
  tracingReceipt : Bool
  statusCode : Bool
  executingAccount : Address
  spentGas : GasConsumed
  output : List Nat
  error : Option String
  logs : List TransferLog
  stateRoot : Option Nat
  deriving DecidableEq, Repr

inductive Effect where
  | metricEmptyCalls
  | gasStateCharge (response : StateChargeResponse)
  | world (request : WorldRequest)
  | action (request : TraceRequest)
  | logCreated (log : TransferLog)
  | substateBuilt (substate : Substate)
  | clearExecutionGas (before after : GasPolicy)
  | settlement (gas : GasConsumed)
  | headerGasUsed (gas : Nat)
  | transactionFields (blockGasUsed spentGas : Nat)
  | receipt (request : TraceRequest)
  deriving DecidableEq, Repr

structure SimpleComplete where
  tx : Transaction
  header : Header
  substate : Substate
  spentGas : GasConsumed
  preRefundGas : Nat
  result : TransactionResult
  blockCumulativeExecutionGas : Nat
  blockCumulativeStateGas : Nat
  receiptContinuation : ReceiptContinuationInput
  effects : List Effect
  deriving DecidableEq, Repr

def gasRemaining (gas : GasPolicy) : Nat := gas.execution
def stateReservoir (gas : GasPolicy) : Int := gas.stateReservoir
def stateGasUsed (gas : GasPolicy) : Int := gas.stateGasUsed
def clearExecutionGas (gas : GasPolicy) : GasPolicy := { gas with execution := 0 }
def stateChargeFromKernelWithCost (input : CompletionInput) (newAccountStateCost : Int) : StateChargeResponse :=
  let result := Eip803x.Generated.StateGasChargeKernel.tryCharge
    input.handoff.gasAvailable.execution
    input.handoff.gasAvailable.stateReservoir
    input.handoff.gasAvailable.stateGasUsed
    input.handoff.gasAvailable.stateGasSpill
    input.handoff.gasAvailable.stateGasSpillRefunded
    newAccountStateCost
  { succeeded := match result.outcome with
      | Eip803x.Generated.StateGasChargeKernel.StateGasChargeOutcome.success => true
      | Eip803x.Generated.StateGasChargeKernel.StateGasChargeOutcome.outOfGas => false
    gas :=
      { execution := result.value
        stateReservoir := result.stateReservoir
        stateGasUsed := result.stateGasUsed
        stateGasSpill := result.stateGasSpill
        stateGasSpillRefunded := result.stateGasSpillRefunded } }

def stateChargeFromKernel (input : CompletionInput) : StateChargeResponse :=
  stateChargeFromKernelWithCost input (sourceNewAccountStateCost : Int)

def dedupAddresses (values : List Address) : List Address :=
  values.foldl (fun seen value => if value ∈ seen then seen else seen ++ [value]) []

def dedupStorageCells (values : List StorageCell) : List StorageCell :=
  values.foldl (fun seen value => if value ∈ seen then seen else seen ++ [value]) []

def accessStorageCells (entries : List AccessEntry) : List StorageCell :=
  entries.flatMap (fun entry => entry.storageKeys.map (fun key => { address := entry.address, key }))

def accessWarmupEntries (input : CompletionInput) : List AccessEntry :=
  if evalSourcePredicate sourceAccessHotPredicate input false false then [] else
    let txEntries := if evalSourcePredicate sourceAccessListPredicate input false false && input.handoff.spec.useTxAccessLists then input.handoff.tx.accessList else []
    let coinbaseEntries :=
      if evalSourcePredicate sourceAccessCoinbasePredicate input false false && input.handoff.spec.addCoinbaseToTxAccessList then
        [{ address := input.handoff.header.gasBeneficiary, storageKeys := [] }]
      else []
    txEntries ++ coinbaseEntries ++
      (if evalSourcePredicate sourceAccessRecipientPredicate input false false then [{ address := input.handoff.recipient, storageKeys := [] }] else []) ++
      [{ address := input.handoff.tx.sender, storageKeys := [] }]

def accessObservation (input : CompletionInput) : AccessObservation :=
  let entries := accessWarmupEntries input
  { addresses := dedupAddresses (entries.map (fun entry => entry.address))
    storageCells := dedupStorageCells (accessStorageCells entries) }

-- IGasPolicy.GetPreRefundGas: txGasLimit - remaining execution gas - state reservoir,
-- falling back to the full limit when the signed result is out of range. Clearing execution gas
-- on an EIP-8037 charge halt does not clear the reservoir, so this remains nonzero on partial gas.
def preRefundGas (gasLimit : Nat) (gas : GasPolicy) : Nat :=
  let candidate : Int := (gasLimit : Int) - (gas.execution : Int) - gas.stateReservoir
  if candidate >= 0 && candidate <= (Eip803x.Generated.StateGasChargeKernel.uint64Max : Int) then Int.toNat candidate else gasLimit
def combineBlockGas (execution state : Nat) : Nat :=
  max execution state
def effectiveBlockGas (gas : GasConsumed) : Nat :=
  if gas.blockGas > 0 || gas.blockStateGas > 0 then gas.blockGas else gas.spentGas

def payValueAmount (input : CompletionInput) : Nat :=
  if isWarmup input.handoff.options then min input.handoff.tx.value input.world.senderBalance else input.handoff.tx.value

def normalCounters (input : CompletionInput) : Bool :=
  Eip803x.Generated.SystemTransactionRoutingKernel.participatesInNormalBlockCounters
    { commit := isCommit input.handoff.options
      restore := isRestore input.handoff.options
      skipValidation := isSkipValidation input.handoff.options
      warmup := isWarmup input.handoff.options
      buildUp := isBuildUp input.handoff.options }
    input.handoff.parallel

def chargeApplies (input : CompletionInput) (selfSend : Bool) : Bool :=
  evalSourcePredicate sourceNewAccountPredicate input selfSend false

def writeApplies (input : CompletionInput) (selfSend oog : Bool) : Bool :=
  evalSourcePredicate sourceRecipientPredicate input selfSend oog

def logApplies (input : CompletionInput) (selfSend oog : Bool) : Bool :=
  evalSourcePredicate sourceLogPredicate input selfSend oog

def buildUpApplies (input : CompletionInput) : Bool :=
  evalSourcePredicate sourceBuildUpPredicate input false false

def feeCollectorRequest (input : CompletionInput) (amount : Nat) : List Effect :=
  match input.handoff.spec.feeCollector with
  | some address => if amount != 0 then [.world (.addOrCreate address amount input.handoff.spec)] else []
  | none => []

def feeEffects (input : CompletionInput) (_substate : Substate) (spent : GasConsumed) (_statusFailure : Bool) : List Effect :=
  let fees := input.handoff.premiumPerGas * spent.spentGas
  let beneficiaryRequest := [.world (.addOrCreate input.handoff.header.gasBeneficiary fees input.handoff.spec)]
  let effectiveBaseFee := min input.handoff.header.baseFeePerGas input.handoff.opcodeGasPrice
  let eip1559Fees := if !input.handoff.tx.isFree then effectiveBaseFee * spent.spentGas else 0
  let collectedBase := if input.handoff.spec.eip1559Enabled then eip1559Fees else 0
  let collectedBlob := if input.handoff.tx.supportsBlobs && input.handoff.spec.eip4844FeeCollectorEnabled then input.handoff.blobBaseFee else 0
  let feeTrace := if input.handoff.tracer.isTracingFees then [.action (.fees fees (eip1559Fees + input.handoff.blobBaseFee))] else []
  beneficiaryRequest ++ feeCollectorRequest input (collectedBase + collectedBlob) ++ feeTrace

def receiptInput (input : CompletionInput) (substate : Substate) (spent : GasConsumed) (statusFailure : Bool) : ReceiptContinuationInput :=
  let root := if input.handoff.tracer.isTracingReceipt && !input.handoff.spec.eip658Enabled then
      input.world.stateRootAfterRecalculate else none
  if statusFailure then
    let output := if substate.shouldRevert then substate.output else []
    let error := match substate.error with
      | some value => some value
      | none => match substate.exception with
          | EvmException.none => none
          | EvmException.outOfGas => some "OutOfGas"
    { tracingReceipt := input.handoff.tracer.isTracingReceipt
      statusCode := true
      executingAccount := input.handoff.recipient
      spentGas := spent
      output
      error
      logs := []
      stateRoot := root }
  else
    { tracingReceipt := input.handoff.tracer.isTracingReceipt
      statusCode := false
      executingAccount := input.handoff.recipient
      spentGas := spent
      output := substate.output
      error := none
      logs := substate.logs
      stateRoot := root }

__OPERATION_DEFINITIONS__

__RUN__

end SimpleTransferCompletionExtractor.Generated
""";
}
