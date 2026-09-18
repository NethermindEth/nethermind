// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.OrdinaryStaticAdmissionExtractor;

/// <summary>Emits the fixed-width Lean kernel described by the source-derived IR.</summary>
/// <remarks>
/// The emitter names the admitted branch order, then selects branch comparisons and predicates
/// from guards and shapes lowered from Roslyn. Constants and field layouts are the target-language
/// representation of source-width operations.
/// </remarks>
internal static class LeanEmitter
{
    internal static byte[] Emit(IrDocument document, string irSha256)
    {
        RequireSourceLoweredGuardPaths(document);
        IrBranch sender = Branch(document, "sender-absent");
        IrBranch nonce = Branch(document, "nonce-overflow");
        IrBranch intrinsicCap = Branch(document, "intrinsic-cap");
        IrBranch executionIntrinsic = Branch(document, "execution-intrinsic");
        IrBranch floorIntrinsic = Branch(document, "floor-intrinsic");
        IrBranch minimumIntrinsic = Branch(document, "minimum-intrinsic");
        IrBranch eip8037Limit = Branch(document, "eip8037-block-limit");
        IrBranch legacyLimit = Branch(document, "legacy-block-limit");
        IrShape isAboveInitCode = Shape(document, "isAboveInitCode");
        IrShape supportsAuthorizationList = Shape(document, "supportsAuthorizationList");
        IrShape setCodeValue = Shape(document, "setCodeValue");
        IrShape setCodeNoCreation = Shape(document, "setCodeNoCreation");
        IrShape authorizationList = Shape(document, "setCodeAuthorizationList");
        IrShape standardGas = Shape(document, "intrinsicStandardGas");
        IrShape minRequiredGas = Shape(document, "intrinsicMinRequiredGasLimit");
        IrShape intrinsicExceedsCap = Shape(document, "intrinsicExceedsCap");
        IrShape skipValidation = Shape(document, "skipValidation");
        IrCallMapping availablePolicyBridge = Mapping(document, "available-policy-bridge");
        const string uint64Maximum = "uint64Max";
        const string gasLimitCap = "txGasLimitCap";
        const string authorizationLengthInput = "input.authorizationListLength";
        const string nonceInput = "input.nonce";
        const string standardInput = "input.standardValue";
        const string floorInput = "input.floorValue";
        const string transactionGasLimit = "input.txGasLimit";
        const string normalizedStandardInput = "normalizeUInt64 input.standardValue";
        const string normalizedFloorInput = "normalizeUInt64 input.floorValue";
        const string minimumInput = "minRequiredGasLimit input";
        const string normalizedHeaderGasLimit = "normalizeUInt64 input.headerGasLimit";
        const string legacyAllowance = "legacyGasAllowance input";
        string setCodeLiteral = Constant(setCodeValue, "4");
        string validateExpression = skipValidation.ConditionKind is IrConditionKind.NegatedPredicate
            ? "!input.skipValidation"
            : "input.skipValidation";
        string initCodeComparison = Compare(isAboveInitCode.ComparisonOperator,
            "(input.dataLength : Int)", "input.maxInitCodeSize");
        string setCodeComparison = Compare(supportsAuthorizationList.ComparisonOperator,
            "input.transactionType", setCodeLiteral);
        string setCodeExpression = setCodeComparison.Replace(setCodeLiteral, "setCodeType", StringComparison.Ordinal);
        string authorizationLength = Constant(authorizationList, "0");
        string authorizationNullCase = authorizationList.SourceExpression.Contains("null", StringComparison.Ordinal)
            ? "!input.authorizationListPresent || "
            : string.Empty;
        string standardGasExpression = Reduction(standardGas.Reduction,
            "input.standardValue", "int64ToUInt64 input.standardStateReservoir");
        string minimumGasExpression = Reduction(minRequiredGas.Reduction,
            "standardGasTotal input", "input.floorValue");
        string initializationGasLimit = BridgeInput(availablePolicyBridge, 0);
        string initializationExecutionGas = BridgeInput(availablePolicyBridge, 1);
        string initializationStateGas = BridgeInput(availablePolicyBridge, 2);
        string initializationFork = BridgeInput(availablePolicyBridge, 3);
        string initializationCap = BridgeInput(availablePolicyBridge, 4);

        RequirePredicateShape(isAboveInitCode, IrConditionKind.Conjunction,
            IrComparisonOperator.GreaterThan,
            ["IsContractCreation", "IsEip3860Enabled", "DataLength", "MaxInitCodeSize"],
            "IsAboveInitCode");
        RequirePredicateShape(setCodeNoCreation, IrConditionKind.Predicate, IrComparisonOperator.None,
            ["IsContractCreation", "NotAllowedCreateTransaction", "ValidationResult", "Success"],
            "ValidateNoContractCreation");
        RequirePredicateShape(authorizationList, IrConditionKind.Comparison, IrComparisonOperator.Equal,
            ["AuthorizationList", "Length", "MissingAuthorizationList", "Success"],
            "ValidateAuthorizationList");
        RequirePredicateShape(intrinsicExceedsCap, IrConditionKind.Disjunction,
            IrComparisonOperator.GreaterThan, ["execution", "floor", "cap"], "IntrinsicGas.ExceedsCap");
        RequirePredicateShape(standardGas, IrConditionKind.Predicate, IrComparisonOperator.None,
            ["GetRemainingGas", "GetStateReservoir", "Standard"], "IntrinsicGas.StandardGas");
        RequirePredicateShape(minRequiredGas, IrConditionKind.Predicate, IrComparisonOperator.None,
            ["Math", "Max", "StandardGas", "GetRemainingGas", "FloorGas"],
            "IntrinsicGas.MinRequiredGasLimit");
        RequirePredicateShape(supportsAuthorizationList, IrConditionKind.Comparison,
            IrComparisonOperator.Equal, ["txType", "SetCode"], "TxType.SupportsAuthorizationList");
        RequirePredicateShape(skipValidation, IrConditionKind.NegatedPredicate,
            IrComparisonOperator.None, ["SkipValidation"], "ValidateStatic.validate");

        StringBuilder builder = new();
        Append(builder, "-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        Append(builder, "-- SPDX-License-Identifier: LGPL-3.0-only");
        Append(builder, "-- Generated by the source-bound OrdinaryStaticAdmissionExtractor. Do not edit.");
        Append(builder, "-- The two functions below are pure return-value projections; no logging or transaction transition is emitted.");
        Append(builder, "-- ErrorDescription/detail text is intentionally an observation exclusion; returnSite remains unique.");
        Append(builder, "import Eip803x.Generated.TransactionGasInitializationKernel");
        Append(builder, "namespace OrdinaryStaticAdmissionExtractor.Generated");
        Append(builder, string.Empty);
        Append(builder, $"def sourceIrSha256 : String := \"{irSha256}\"");
        Append(builder, "def uint64Modulus : Nat := 2 ^ 64");
        Append(builder, "def uint64Max : Nat := uint64Modulus - 1");
        Append(builder, "def uint8Max : Nat := 2 ^ 8 - 1");
        Append(builder, "def intMax : Nat := 2 ^ 31 - 1");
        Append(builder, "def int64SignBit : Nat := 2 ^ 63");
        Append(builder, "def int64Min : Int := -(2 ^ 63 : Int)");
        Append(builder, "def int64Max : Int := (2 ^ 63 : Int) - 1");
        Append(builder, string.Empty);
        Append(builder, "def normalizeUInt64 := Eip803x.Generated.TransactionGasInitializationKernel.normalizeUInt64");
        Append(builder, "def addUInt64 := Eip803x.Generated.TransactionGasInitializationKernel.addUInt64");
        Append(builder, "def subUInt64 := Eip803x.Generated.TransactionGasInitializationKernel.subUInt64");
        Append(builder, "def int64ToUInt64 := Eip803x.Generated.TransactionGasInitializationKernel.int64ToUInt64");
        Append(builder, "def uint64ToInt64 := Eip803x.Generated.TransactionGasInitializationKernel.uint64ToInt64");
        Append(builder, string.Empty);
        Append(builder, "abbrev InitializationOutcome := Eip803x.Generated.TransactionGasInitializationKernel.TransactionGasInitializationOutcome");
        Append(builder, string.Empty);
        Append(builder, "structure InitializationInput where");
        Append(builder, "  gasLimit : Nat");
        Append(builder, "  intrinsicExecutionGas : Nat");
        Append(builder, "  intrinsicStateGas : Int");
        Append(builder, "  eip8037Enabled : Bool");
        Append(builder, "  executionGasLimitCap : Nat");
        Append(builder, "  deriving DecidableEq, Repr");
        Append(builder, string.Empty);
        Append(builder, "abbrev InitializationResult := Eip803x.Generated.TransactionGasInitializationKernel.TransactionGasInitializationResult");
        Append(builder, string.Empty);
        Append(builder, "def tryCreate (input : InitializationInput) : InitializationResult :=");
        Append(builder, "  Eip803x.Generated.TransactionGasInitializationKernel.tryCreate");
        Append(builder, "    input.gasLimit input.intrinsicExecutionGas input.intrinsicStateGas");
        Append(builder, "    input.eip8037Enabled input.executionGasLimitCap");
        Append(builder, string.Empty);
        Append(builder, "inductive ErrorType where");
        Append(builder, "  | none");
        Append(builder, "  | blockGasLimitExceeded");
        Append(builder, "  | gasLimitBelowIntrinsicGas");
        Append(builder, "  | gasLimitBelowFloorGas");
        Append(builder, "  | malformedTransaction");
        Append(builder, "  | nonceOverflow");
        Append(builder, "  | senderNotSpecified");
        Append(builder, "  | transactionSizeOverMaxInitCodeSize");
        Append(builder, "  deriving DecidableEq, Repr");
        Append(builder, string.Empty);
        Append(builder, "inductive EvmExceptionType where");
        Append(builder, "  | none");
        Append(builder, "  deriving DecidableEq, Repr");
        Append(builder, string.Empty);
        Append(builder, "inductive ReturnSite where");
        Append(builder, "  | validateStaticSenderAbsent");
        Append(builder, "  | validateStaticNonceOverflow");
        Append(builder, "  | validateStaticInitcodeOversize");
        Append(builder, "  | validateStaticSetCodeCreation");
        Append(builder, "  | validateStaticSetCodeAuthorization");
        Append(builder, "  | validateStaticIntrinsicCap");
        Append(builder, "  | validateStaticExecutionIntrinsic");
        Append(builder, "  | validateStaticFloorIntrinsic");
        Append(builder, "  | validateGasMinimumIntrinsic");
        Append(builder, "  | validateGasEip8037BlockLimit");
        Append(builder, "  | validateGasLegacyBlockLimit");
        Append(builder, "  | validateGasOk");
        Append(builder, "  | calculateAvailableGasFailure");
        Append(builder, "  | calculateAvailableGasSuccess");
        Append(builder, "  deriving DecidableEq, Repr");
        Append(builder, string.Empty);
        Append(builder, "structure TransactionResult where");
        Append(builder, "  error : ErrorType");
        Append(builder, "  evmExceptionType : EvmExceptionType");
        Append(builder, "  returnSite : ReturnSite");
        Append(builder, "  deriving DecidableEq, Repr");
        Append(builder, string.Empty);
        Append(builder, "def result (error : ErrorType) (returnSite : ReturnSite) : TransactionResult :=");
        Append(builder, "  { error, evmExceptionType := .none, returnSite }");
        Append(builder, string.Empty);
        Append(builder, "structure StaticInput where");
        Append(builder, "  senderPresent : Bool");
        Append(builder, "  nonce : Nat");
        Append(builder, "  toPresent : Bool");
        Append(builder, "  dataLength : Nat");
        Append(builder, "  transactionType : Nat");
        Append(builder, "  authorizationListPresent : Bool");
        Append(builder, "  authorizationListLength : Nat");
        Append(builder, "  txGasLimit : Nat");
        Append(builder, "  headerGasLimit : Nat");
        Append(builder, "  headerGasUsed : Nat");
        Append(builder, "  skipValidation : Bool");
        Append(builder, "  processorParallel : Bool");
        Append(builder, "  eip3860Enabled : Bool");
        Append(builder, "  eip8037Enabled : Bool");
        Append(builder, "  maxInitCodeSize : Int");
        Append(builder, "  standardValue : Nat");
        Append(builder, "  standardStateReservoir : Int");
        Append(builder, "  floorValue : Nat");
        Append(builder, "  deriving DecidableEq, Repr");
        Append(builder, string.Empty);
        Append(builder, $"def setCodeType : Nat := {setCodeLiteral}");
        Append(builder, "def txGasLimitCap : Nat := 16_777_216");
        Append(builder, "def isCreation (input : StaticInput) : Bool := !input.toPresent");
        Append(builder, $"def isSetCode (input : StaticInput) : Bool := {setCodeExpression}");
        Append(builder, "def isAboveInitCode (input : StaticInput) : Bool :=");
        Append(builder, $"  isCreation input && input.eip3860Enabled && {initCodeComparison}");
        Append(builder, "def standardGasTotal (input : StaticInput) : Nat :=");
        Append(builder, $"  {standardGasExpression}");
        Append(builder, "def minRequiredGasLimit (input : StaticInput) : Nat :=");
        Append(builder, $"  {minimumGasExpression}");
        Append(builder, "def legacyGasAllowance (input : StaticInput) : Nat :=");
        Append(builder, "  subUInt64 input.headerGasLimit (if input.processorParallel then 0 else input.headerGasUsed)");
        Append(builder, string.Empty);
        Append(builder, "def validateStatic (input : StaticInput) : TransactionResult :=");
        Append(builder, $"  let validate := {validateExpression}");
        Append(builder, $"  if {SenderGuard(sender)} then result .senderNotSpecified .validateStaticSenderAbsent else");
        Append(builder, $"  if validate && {Compare(nonce.ComparisonOperator, nonceInput, uint64Maximum)} then result .nonceOverflow .validateStaticNonceOverflow else");
        Append(builder, "  if isAboveInitCode input then result .transactionSizeOverMaxInitCodeSize .validateStaticInitcodeOversize else");
        Append(builder, "  if isSetCode input && isCreation input then result .malformedTransaction .validateStaticSetCodeCreation else");
        Append(builder, $"  if isSetCode input && ({authorizationNullCase}{Compare(authorizationList.ComparisonOperator, authorizationLengthInput, authorizationLength)}) then");
        Append(builder, "    result .malformedTransaction .validateStaticSetCodeAuthorization else");
        string capConnective = Connective(intrinsicExceedsCap.ConditionKind, "IntrinsicGas.ExceedsCap");
        Append(builder, $"  if input.eip8037Enabled && ({Compare(intrinsicCap.ComparisonOperator, standardInput, gasLimitCap)} {capConnective} {Compare(intrinsicCap.ComparisonOperator, floorInput, gasLimitCap)}) then");
        Append(builder, "    result .gasLimitBelowIntrinsicGas .validateStaticIntrinsicCap else");
        Append(builder, $"  if {Compare(executionIntrinsic.ComparisonOperator, transactionGasLimit, normalizedStandardInput)} then");
        Append(builder, "    result .gasLimitBelowIntrinsicGas .validateStaticExecutionIntrinsic else");
        Append(builder, $"  if {Compare(floorIntrinsic.ComparisonOperator, transactionGasLimit, normalizedFloorInput)} then");
        Append(builder, "    result .gasLimitBelowFloorGas .validateStaticFloorIntrinsic else");
        Append(builder, $"  if {Compare(minimumIntrinsic.ComparisonOperator, transactionGasLimit, minimumInput)} then");
        Append(builder, "    result .gasLimitBelowIntrinsicGas .validateGasMinimumIntrinsic else");
        Append(builder, $"  if validate && input.eip8037Enabled && {Compare(eip8037Limit.ComparisonOperator, transactionGasLimit, normalizedHeaderGasLimit)} then");
        Append(builder, "    result .blockGasLimitExceeded .validateGasEip8037BlockLimit else");
        Append(builder, $"  if validate && !input.eip8037Enabled && {Compare(legacyLimit.ComparisonOperator, transactionGasLimit, legacyAllowance)} then");
        Append(builder, "    result .blockGasLimitExceeded .validateGasLegacyBlockLimit else");
        Append(builder, "    result .none .validateGasOk");
        Append(builder, string.Empty);
        Append(builder, "def initializationInput (input : StaticInput) : InitializationInput :=");
        Append(builder, $"  {{ gasLimit := {initializationGasLimit}, intrinsicExecutionGas := {initializationExecutionGas},");
        Append(builder, $"    intrinsicStateGas := {initializationStateGas}, eip8037Enabled := {initializationFork},");
        Append(builder, $"    executionGasLimitCap := {initializationCap} }}");
        Append(builder, string.Empty);
        Append(builder, "structure AvailableGasPolicy where");
        Append(builder, "  value : Nat");
        Append(builder, "  stateReservoir : Int");
        Append(builder, "  stateGasUsed : Int");
        Append(builder, "  stateGasSpill : Int");
        Append(builder, "  stateGasSpillRefunded : Int");
        Append(builder, "  deriving DecidableEq, Repr");
        Append(builder, string.Empty);
        Append(builder, "def defaultAvailablePolicy : AvailableGasPolicy :=");
        Append(builder, "  { value := 0, stateReservoir := 0, stateGasUsed := 0, stateGasSpill := 0, stateGasSpillRefunded := 0 }");
        Append(builder, "def policyFromInitialization (initialization : InitializationResult) : AvailableGasPolicy :=");
        Append(builder, "  {");
        EmitPolicyAssignments(builder, availablePolicyBridge);
        Append(builder, "  }");
        Append(builder, string.Empty);
        Append(builder, "structure AvailableGasResult where");
        Append(builder, "  result : TransactionResult");
        Append(builder, "  available : AvailableGasPolicy");
        Append(builder, "  initialization : InitializationResult");
        Append(builder, "  deriving DecidableEq, Repr");
        Append(builder, string.Empty);
        Append(builder, "def calculateAvailableGas (input : InitializationInput) : AvailableGasResult :=");
        Append(builder, "  let initialization := tryCreate input");
        Append(builder, "  match initialization.outcome with");
        Append(builder, "  | .intrinsicGasExceedsLimit =>");
        Append(builder, "    { result := result .gasLimitBelowIntrinsicGas .calculateAvailableGasFailure, available := defaultAvailablePolicy, initialization }");
        Append(builder, "  | .success =>");
        Append(builder, "    { result := result .none .calculateAvailableGasSuccess, available := policyFromInitialization initialization, initialization }");
        Append(builder, string.Empty);
        Append(builder, "end OrdinaryStaticAdmissionExtractor.Generated");
        return Encoding.UTF8.GetBytes(builder.ToString() + Environment.NewLine);
    }

    private static IrBranch Branch(IrDocument document, string id) =>
        document.Branches.Single(branch => branch.Id == id);

    private static void RequireSourceLoweredGuardPaths(IrDocument document)
    {
        RequireGuard(document, "sender-absent", 1, IrConditionKind.Comparison,
            IrComparisonOperator.IsNull, ["SenderAddress"]);
        RequireGuardSource(document, "sender-absent", 0, "tx.SenderAddressisnull");
        RequireGuard(document, "nonce-overflow", 1, IrConditionKind.Conjunction,
            IrComparisonOperator.Equal, ["validate", "Nonce", "MaxValue"]);
        RequireGuardSource(document, "nonce-overflow", 0, "validate&&tx.Nonce==ulong.MaxValue");
        RequireGuard(document, "initcode-oversize", 1, IrConditionKind.Predicate,
            IrComparisonOperator.None, ["IsAboveInitCode"]);
        RequireGuardSource(document, "initcode-oversize", 0, "tx.IsAboveInitCode(spec)");

        IrBranch setCodeCreation = Branch(document, "setcode-creation");
        if (setCodeCreation.GuardPath.Length != 2 ||
            !GuardHas(setCodeCreation.GuardPath[0], IrConditionKind.Predicate,
                IrComparisonOperator.None, "SupportsAuthorizationList", positive: true) ||
            !GuardHas(setCodeCreation.GuardPath[1], IrConditionKind.NegatedPredicate,
                IrComparisonOperator.NotEqual, "noCreation", positive: false) ||
            setCodeCreation.GuardPath[0].Source != "tx.SupportsAuthorizationList" ||
            setCodeCreation.GuardPath[1].Source != "!noCreation")
            throw new ExtractionException("Source-lowered SetCode creation guard path changed.");

        IrBranch setCodeAuthorization = Branch(document, "setcode-auth-empty");
        if (setCodeAuthorization.GuardPath.Length != 2 ||
            !GuardHas(setCodeAuthorization.GuardPath[0], IrConditionKind.Predicate,
                IrComparisonOperator.None, "SupportsAuthorizationList", positive: true) ||
            !GuardHas(setCodeAuthorization.GuardPath[1], IrConditionKind.NegatedPredicate,
                IrComparisonOperator.NotEqual, "authList", positive: false) ||
            setCodeAuthorization.GuardPath[0].Source != "tx.SupportsAuthorizationList" ||
            setCodeAuthorization.GuardPath[1].Source != "!authList")
            throw new ExtractionException("Source-lowered SetCode authorization guard path changed.");

        RequireGuard(document, "intrinsic-cap", 1, IrConditionKind.Conjunction,
            IrComparisonOperator.None, ["IsEip8037Enabled", "ExceedsCap"]);
        RequireGuardSource(document, "intrinsic-cap", 0,
            "spec.IsEip8037Enabled&&intrinsicGas.ExceedsCap(Eip7825Constants.DefaultTxGasLimitCap,outulongexecution,outulongfloor)");
        RequireOrderedGuard(document, "execution-intrinsic", "tx.GasLimit", "standardGasUsed");
        RequireOrderedGuard(document, "floor-intrinsic", "tx.GasLimit", "floorGasUsed");
        RequireOrderedGuard(document, "minimum-intrinsic", "tx.GasLimit", "minGasRequired");
        IrBranch eip8037 = Branch(document, "eip8037-block-limit");
        if (eip8037.GuardPath.Length != 3 ||
            !GuardHas(eip8037.GuardPath[0], IrConditionKind.Predicate,
                IrComparisonOperator.None, "validate", positive: true) ||
            !GuardHas(eip8037.GuardPath[1], IrConditionKind.Predicate,
                IrComparisonOperator.None, "IsEip8037Enabled", positive: true) ||
            !GuardHasOrdered(eip8037.GuardPath[2], "tx.GasLimit", "header.GasLimit") ||
            eip8037.GuardPath[0].Source != "validate" ||
            eip8037.GuardPath[1].Source != "spec.IsEip8037Enabled")
            throw new ExtractionException("Source-lowered EIP-8037 block-limit guard path changed.");

        IrBranch legacy = Branch(document, "legacy-block-limit");
        if (legacy.GuardPath.Length != 3 ||
            !GuardHas(legacy.GuardPath[0], IrConditionKind.Predicate,
                IrComparisonOperator.None, "validate", positive: true) ||
            !GuardHas(legacy.GuardPath[1], IrConditionKind.NegatedPredicate,
                IrComparisonOperator.NotEqual, "IsEip8037Enabled", positive: false) ||
            !GuardHasOrdered(legacy.GuardPath[2], "tx.GasLimit", "maxTransactionGasLimit") ||
            !legacy.GuardPath[1].IsSyntheticFallthrough ||
            legacy.GuardPath[0].Source != "validate" ||
            legacy.GuardPath[1].Source != "!(spec.IsEip8037Enabled)")
            throw new ExtractionException("Source-lowered legacy block-limit fallthrough changed.");

        if (Branch(document, "ok").GuardPath.Length != 0)
            throw new ExtractionException("Source-lowered validation success path unexpectedly gained a guard.");
    }

    private static void RequireGuard(
        IrDocument document,
        string branchId,
        int pathLength,
        IrConditionKind conditionKind,
        IrComparisonOperator comparisonOperator,
        IReadOnlyList<string> operands)
    {
        IrBranch branch = Branch(document, branchId);
        if (branch.GuardPath.Length != pathLength ||
            !GuardHas(branch.GuardPath[^1], conditionKind, comparisonOperator, operands, positive: true))
            throw new ExtractionException($"Source-lowered guard path changed for branch {branchId}.");
    }

    private static void RequireOrderedGuard(IrDocument document, string branchId, string left, string right)
    {
        IrBranch branch = Branch(document, branchId);
        if (branch.GuardPath.Length != 1 || !GuardHasOrdered(branch.GuardPath[0], left, right))
            throw new ExtractionException($"Source-lowered comparison guard changed for branch {branchId}.");
    }

    private static bool GuardHasOrdered(IrGuard guard, string left, string right) =>
        guard.ConditionKind == IrConditionKind.Comparison &&
        (guard.ComparisonOperator is IrComparisonOperator.LessThan or IrComparisonOperator.LessThanOrEqual
            or IrComparisonOperator.GreaterThan or IrComparisonOperator.GreaterThanOrEqual) &&
        string.Equals(guard.Source, $"{left}{ComparisonToken(guard.ComparisonOperator)}{right}", StringComparison.Ordinal);

    private static void RequireGuardSource(IrDocument document, string branchId, int pathIndex, string source)
    {
        IrBranch branch = Branch(document, branchId);
        if (pathIndex < 0 || pathIndex >= branch.GuardPath.Length ||
            !string.Equals(branch.GuardPath[pathIndex].Source, source, StringComparison.Ordinal))
            throw new ExtractionException($"Source-lowered guard source changed for branch {branchId}.");
    }

    private static bool GuardHas(
        IrGuard guard,
        IrConditionKind conditionKind,
        IrComparisonOperator comparisonOperator,
        string operand,
        bool positive) =>
        guard.ConditionKind == conditionKind &&
        guard.ComparisonOperator == comparisonOperator &&
        ContainsGuardOperand(guard, operand) &&
        (positive ? !GuardIsNegated(guard, operand) : GuardIsNegated(guard, operand));

    private static bool GuardHas(
        IrGuard guard,
        IrConditionKind conditionKind,
        IrComparisonOperator comparisonOperator,
        IReadOnlyList<string> operands,
        bool positive) =>
        guard.ConditionKind == conditionKind &&
        guard.ComparisonOperator == comparisonOperator &&
        operands.All(operand => ContainsGuardOperand(guard, operand)) &&
        (positive
            ? operands.All(operand => !GuardIsNegated(guard, operand))
            : guard.Source.Contains("!", StringComparison.Ordinal));

    private static bool GuardIsNegated(IrGuard guard, string operand) =>
        guard.Source.Contains($"!{operand}", StringComparison.Ordinal) ||
        (guard.Source.Contains("!(", StringComparison.Ordinal) &&
            guard.Source.Contains(operand, StringComparison.Ordinal));

    private static bool ContainsGuardOperand(IrGuard guard, string operand) =>
        guard.Operands.Any(candidate => candidate.Name == operand || candidate.Source == operand) ||
        guard.Source.Contains(operand, StringComparison.Ordinal);

    private static IrShape Shape(IrDocument document, string id) =>
        document.Shapes.Single(shape => shape.Id == id);

    private static void RequirePredicateShape(
        IrShape shape,
        IrConditionKind conditionKind,
        IrComparisonOperator comparisonOperator,
        IReadOnlyList<string> operands,
        string name)
    {
        if (shape.ConditionKind != conditionKind || shape.ComparisonOperator != comparisonOperator ||
            operands.Any(operand => !shape.Operands.Any(candidate =>
                candidate.Name == operand || candidate.Source == operand)))
            throw new ExtractionException($"Source-lowered semantic shape changed for {name}.");
    }

    private static string Connective(IrConditionKind conditionKind, string name) =>
        conditionKind switch
        {
            IrConditionKind.Conjunction => "&&",
            IrConditionKind.Disjunction => "||",
            _ => throw new ExtractionException($"Source-lowered connective is unsupported for {name}."),
        };

    private static IrCallMapping Mapping(IrDocument document, string id) =>
        document.CallMappings.Single(mapping => mapping.Id == id);

    private static string BridgeInput(IrCallMapping mapping, int index)
    {
        if (mapping.ArgumentExpressions.Length != 5)
            throw new ExtractionException("The source-derived available-policy bridge must have five arguments.");
        string expression = mapping.ArgumentExpressions[index];
        return expression switch
        {
            "gasLimit" => "input.txGasLimit",
            "intrinsicGas.Value" => "input.standardValue",
            "intrinsicGas.StateReservoir" => "input.standardStateReservoir",
            "spec.IsEip8037Enabled" => "input.eip8037Enabled",
            "Eip7825Constants.DefaultTxGasLimitCap" => "txGasLimitCap",
            _ => throw new ExtractionException($"Unsupported source bridge argument '{expression}'."),
        };
    }

    private static void EmitPolicyAssignments(StringBuilder builder, IrCallMapping mapping)
    {
        if (!string.Equals(mapping.FailureDefaultExpression, "default", StringComparison.Ordinal))
            throw new ExtractionException("The source-derived available-policy bridge no longer defaults its out value on failure.");

        for (int i = 0; i < mapping.OutputFields.Length; i++)
        {
            string field = mapping.OutputFields[i] switch
            {
                "Value" => "value",
                "StateReservoir" => "stateReservoir",
                "StateGasUsed" => "stateGasUsed",
                "StateGasSpill" => "stateGasSpill",
                "StateGasSpillRefunded" => "stateGasSpillRefunded",
                _ => throw new ExtractionException($"Unsupported source bridge output field '{mapping.OutputFields[i]}'."),
            };
            string sourceExpression = mapping.OutputExpressions[i];
            string sourceField = sourceExpression switch
            {
                "result.Value" => "value",
                "result.StateReservoir" => "stateReservoir",
                "result.StateGasUsed" => "stateGasUsed",
                "result.StateGasSpill" => "stateGasSpill",
                "result.StateGasSpillRefunded" => "stateGasSpillRefunded",
                _ => throw new ExtractionException($"Unsupported source bridge output expression '{sourceExpression}'."),
            };
            string separator = i + 1 == mapping.OutputFields.Length ? string.Empty : ",";
            Append(builder, $"    {field} := initialization.{sourceField}{separator}");
        }
    }

    private static string SenderGuard(IrBranch branch) => branch.ComparisonOperator switch
    {
        IrComparisonOperator.IsNull => "!input.senderPresent",
        IrComparisonOperator.IsNotNull => "input.senderPresent",
        _ => throw new ExtractionException("Sender nullness was not lowered to a supported typed operator."),
    };

    private static string Compare(IrComparisonOperator comparisonOperator, string left, string right) => comparisonOperator switch
    {
        IrComparisonOperator.Equal => $"{left} == {right}",
        IrComparisonOperator.NotEqual => $"{left} != {right}",
        IrComparisonOperator.LessThan => $"{left} < {right}",
        IrComparisonOperator.LessThanOrEqual => $"{left} <= {right}",
        IrComparisonOperator.GreaterThan => $"{left} > {right}",
        IrComparisonOperator.GreaterThanOrEqual => $"{left} >= {right}",
        _ => throw new ExtractionException($"The source-derived operator {comparisonOperator} is not a supported comparison."),
    };

    private static string ComparisonToken(IrComparisonOperator comparisonOperator) => comparisonOperator switch
    {
        IrComparisonOperator.Equal => "==",
        IrComparisonOperator.NotEqual => "!=",
        IrComparisonOperator.LessThan => "<",
        IrComparisonOperator.LessThanOrEqual => "<=",
        IrComparisonOperator.GreaterThan => ">",
        IrComparisonOperator.GreaterThanOrEqual => ">=",
        _ => throw new ExtractionException($"Unsupported source comparison operator {comparisonOperator}."),
    };

    private static string Constant(IrShape shape, string fallback)
    {
        string? value = shape.Operands
            .Where(operand => operand.Kind is IrOperandKind.Constant)
            .Select(operand => operand.Name)
            .FirstOrDefault(name => name.All(char.IsDigit));
        return value ?? throw new ExtractionException($"Shape {shape.Id} lost its source constant (expected {fallback}).");
    }

    private static string Reduction(IrReductionKind reduction, string left, string right) => reduction switch
    {
        IrReductionKind.Addition => $"addUInt64 {left} ({right})",
        IrReductionKind.Subtraction => $"subUInt64 {left} ({right})",
        IrReductionKind.Minimum => $"Nat.min ({left}) ({right})",
        IrReductionKind.Maximum => $"Nat.max ({left}) ({right})",
        _ => throw new ExtractionException("The source-derived arithmetic reduction is unsupported."),
    };

    private static void Append(StringBuilder builder, string line) => builder.AppendLine(line);
}
