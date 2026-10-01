-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.Semantics

namespace SimpleTransferCompletionExtractor.StageB.Runtime

def FunctionSymbolsUnique (program : Program) : Prop :=
  (program.functions.map (fun function => function.signature.symbol)).Nodup

def BlockOrdinalsUnique (function : Function) : Prop :=
  (function.blocks.map (fun block => block.ordinal)).Nodup

def ParameterSymbolsUnique (function : Function) : Prop :=
  (function.signature.parameters.map (fun parameter => parameter.symbol)).Nodup

def ProgramParameterSymbolsUnique (program : Program) : Prop :=
  ∀ function ∈ program.functions, ParameterSymbolsUnique function

def programParameterSymbolsUniqueValid (program : Program) : Bool :=
  program.functions.all (fun function =>
    decide (function.signature.parameters.map (fun parameter => parameter.symbol)).Nodup)

theorem programParameterSymbolsUniqueValid_iff (program : Program) :
    programParameterSymbolsUniqueValid program = true ↔ ProgramParameterSymbolsUnique program := by
  simp [programParameterSymbolsUniqueValid, ProgramParameterSymbolsUnique, ParameterSymbolsUnique]

theorem ProgramParameterSymbolsUnique.function (program : Program)
    (unique : ProgramParameterSymbolsUnique program) (function : Function)
    (member : function ∈ program.functions) : ParameterSymbolsUnique function := by
  exact unique function member

def ProgramDictionaryShape (program : Program) : Prop :=
  FunctionSymbolsUnique program ∧
    (∃ function ∈ program.functions, function.signature.symbol = program.entry) ∧
    ∀ function ∈ program.functions, BlockOrdinalsUnique function ∧
      ∃ block ∈ function.blocks, block.ordinal = function.entryBlock

def programDictionaryShapeValid (program : Program) : Bool :=
  decide (program.functions.map (fun function => function.signature.symbol)).Nodup &&
    program.functions.any (fun function => function.signature.symbol == program.entry) &&
    program.functions.all (fun function =>
      decide (function.blocks.map (fun block => block.ordinal)).Nodup &&
        function.blocks.any (fun block => block.ordinal == function.entryBlock))

theorem programDictionaryShapeValid_iff (program : Program) :
    programDictionaryShapeValid program = true ↔ ProgramDictionaryShape program := by
  simp [programDictionaryShapeValid, ProgramDictionaryShape, FunctionSymbolsUnique,
    BlockOrdinalsUnique, and_assoc]

private theorem equal_of_mem_of_nodup_keys {α β : Type} (key : α → β)
    (values : List α) (left right : α) (unique : (values.map key).Nodup)
    (leftMember : left ∈ values) (rightMember : right ∈ values)
    (sameKey : key left = key right) : left = right := by
  induction values with
  | nil => simp at leftMember
  | cons head tail induction =>
      simp only [List.map_cons, List.nodup_cons] at unique
      simp only [List.mem_cons] at leftMember rightMember
      rcases leftMember with rfl | leftMember
      · rcases rightMember with rfl | rightMember
        · rfl
        · exact False.elim (unique.1 (List.mem_map.mpr ⟨right, rightMember, sameKey.symm⟩))
      · rcases rightMember with rfl | rightMember
        · exact False.elim (unique.1 (List.mem_map.mpr ⟨left, leftMember, sameKey⟩))
        · exact induction unique.2 leftMember rightMember

theorem FunctionSymbolsUnique.function_eq (program : Program)
    (unique : FunctionSymbolsUnique program) (left right : Function)
    (leftMember : left ∈ program.functions) (rightMember : right ∈ program.functions)
    (sameSymbol : left.signature.symbol = right.signature.symbol) : left = right := by
  exact equal_of_mem_of_nodup_keys (fun (function : Function) => function.signature.symbol)
    program.functions left right unique leftMember rightMember sameSymbol

theorem BlockOrdinalsUnique.block_eq (function : Function)
    (unique : BlockOrdinalsUnique function) (left right : Block)
    (leftMember : left ∈ function.blocks) (rightMember : right ∈ function.blocks)
    (sameOrdinal : left.ordinal = right.ordinal) : left = right := by
  exact equal_of_mem_of_nodup_keys (fun (block : Block) => block.ordinal)
    function.blocks left right unique leftMember rightMember sameOrdinal

private theorem find_keys_eq_some_iff {α β : Type} [BEq β] [LawfulBEq β]
    (key : α → β) (values : List α) (wanted : β) (value : α)
    (unique : (values.map key).Nodup) :
    values.find? (fun candidate => key candidate == wanted) = some value ↔
      value ∈ values ∧ key value = wanted := by
  constructor
  · intro found
    exact ⟨List.mem_of_find?_eq_some found, by simpa using List.find?_some found⟩
  · rintro ⟨member, keyEqual⟩
    cases found : values.find? (fun candidate => key candidate == wanted) with
    | none =>
        have absent := List.find?_eq_none.mp found value member
        simp [keyEqual] at absent
    | some selected =>
        have selectedMember := List.mem_of_find?_eq_some found
        have selectedMatches : key selected = wanted := by simpa using List.find?_some found
        have same := equal_of_mem_of_nodup_keys key values selected value unique
          selectedMember member (selectedMatches.trans keyEqual.symm)
        exact congrArg some same

theorem FunctionSymbolsUnique.find_eq_some_iff (program : Program)
    (unique : FunctionSymbolsUnique program) (symbol : String) (function : Function) :
    program.functions.find? (fun candidate => candidate.signature.symbol == symbol) = some function ↔
      function ∈ program.functions ∧ function.signature.symbol = symbol := by
  exact find_keys_eq_some_iff (fun (candidate : Function) => candidate.signature.symbol)
    program.functions symbol function unique

theorem BlockOrdinalsUnique.find_eq_some_iff (function : Function)
    (unique : BlockOrdinalsUnique function) (ordinal : Int) (block : Block) :
    (if ordinal < 0 then none else
      function.blocks.find? (fun candidate => candidate.ordinal == Int.toNat ordinal)) = some block ↔
      block ∈ function.blocks ∧ (block.ordinal : Int) = ordinal := by
  by_cases negative : ordinal < 0
  · have impossible : ¬ (block.ordinal : Int) = ordinal := by omega
    simp [negative, impossible]
  · rw [if_neg negative, find_keys_eq_some_iff (fun (candidate : Block) => candidate.ordinal)
      function.blocks ordinal.toNat block unique]
    constructor
    · rintro ⟨member, equal⟩
      exact ⟨member, by omega⟩
    · rintro ⟨member, equal⟩
      exact ⟨member, by omega⟩

-- Generic program admission does not establish either dictionary-key uniqueness property.
set_option maxRecDepth 10000 in
set_option maxHeartbeats 8000000 in
theorem generated_program_dictionaryShape : ProgramDictionaryShape Generated.program := by
  apply (programDictionaryShapeValid_iff Generated.program).mp
  decide

set_option maxRecDepth 10000 in
set_option maxHeartbeats 8000000 in
theorem generated_program_parameterSymbolsUnique :
    ProgramParameterSymbolsUnique Generated.program := by
  apply (programParameterSymbolsUniqueValid_iff Generated.program).mp
  decide

example (function : Function) (first second : Parameter)
    (sameSymbol : first.symbol = second.symbol) :
    ¬ ParameterSymbolsUnique { function with
      signature := { function.signature with parameters := [first, second] } } := by
  simp [ParameterSymbolsUnique, List.nodup_cons, sameSymbol]

example (program : Program) (first second : Function) (parameter : Parameter) :
    programParameterSymbolsUniqueValid { program with functions :=
      [first, { second with
        signature := { second.signature with parameters := [parameter, parameter] } }] } = false := by
  simp [programParameterSymbolsUniqueValid]

example (program : Program) (first second : Function)
    (sameSymbol : first.signature.symbol = second.signature.symbol) :
    ¬ FunctionSymbolsUnique { program with functions := [first, second] } := by
  simp [FunctionSymbolsUnique, List.nodup_cons, sameSymbol]

example (program : Program) :
    programDictionaryShapeValid { program with functions := [] } = false := by
  rfl

example (program : Program) (function : Function) :
    programDictionaryShapeValid { program with functions := [{ function with blocks := [] }] } = false := by
  simp [programDictionaryShapeValid]

example (program : Program) (function : Function) (block : Block) :
    ProgramDictionaryShape { program with
      entry := "A"
      functions :=
        [{ function with
            signature := { function.signature with symbol := "A" }
            entryBlock := block.ordinal
            blocks := [block] },
         { function with
            signature := { function.signature with symbol := "a" }
            entryBlock := block.ordinal
            blocks := [block] }] } := by
  simp [ProgramDictionaryShape, FunctionSymbolsUnique, BlockOrdinalsUnique, List.nodup_cons]

example (function : Function) (first second : Block) (sameOrdinal : first.ordinal = second.ordinal) :
    ¬ BlockOrdinalsUnique { function with blocks := [first, second] } := by
  simp [BlockOrdinalsUnique, List.nodup_cons, sameOrdinal]

/-!
## Frozen local-call graph closure

This is a structural property of the emitted program data. It checks every operation root,
optional branch root, and recursive child. It does not establish dynamic reachability or the
failure order of malformed programs.
-/

def symbolPrecedes (earlier later : String) : List String → Bool
  | [] => false
  | symbol :: rest =>
      if symbol == earlier then rest.contains later else symbolPrecedes earlier later rest

def receiverChildValid (childCount : Nat) (receiverChild : Int) : Bool :=
  if receiverChild < 0 then true else decide (receiverChild.toNat < childCount)

def argumentReferencesValid (childCount parameterCount : Nat)
    (arguments : List Argument) : Bool :=
  arguments.all (fun argument =>
    decide (argument.child < childCount) && decide (argument.ordinal < parameterCount))

def argumentOrdinalsUnique (arguments : List Argument) : Bool :=
  decide (arguments.map (fun argument => argument.ordinal)).Nodup

def descriptorShapeValid (kind : OperationKind) (childCount : Nat)
    (call : Option Call) : Bool :=
  if kind == .invocation || kind == .objectCreation then
    match call with
    | none => false
    | some descriptor =>
        descriptor.arguments.length == descriptor.target.member.parameters.length &&
          argumentReferencesValid childCount descriptor.target.member.parameters.length
            descriptor.arguments &&
          argumentOrdinalsUnique descriptor.arguments
  else true

def calleeOrderShapeValid (program : Program) : Bool :=
  decide program.calleeBeforeCaller.Nodup &&
    program.calleeBeforeCaller.length == program.functions.length &&
    program.functions.all (fun function =>
      program.calleeBeforeCaller.contains function.signature.symbol) &&
    program.calleeBeforeCaller.all (fun symbol =>
      program.functions.any (fun function => function.signature.symbol == symbol))

def localParameterShapeValid (childCount : Nat) (callee : Function) (call : Call) : Bool :=
  callee.signature.parameters.length == call.target.member.parameters.length &&
    if call.arguments.isEmpty then
      callee.signature.parameters.isEmpty ||
        callee.signature.parameters.length == childCount
    else
      call.arguments.length == callee.signature.parameters.length &&
        argumentReferencesValid childCount callee.signature.parameters.length call.arguments &&
        argumentOrdinalsUnique call.arguments

def localCallSiteValid (program : Program) (caller : String) (childCount : Nat)
    (call : Call) : Bool :=
  if call.target.kind == .local then
    match program.functions.find? (fun function =>
        function.signature.symbol == call.target.body) with
    | none => false
    | some callee =>
        receiverChildValid childCount call.receiverChild &&
          localParameterShapeValid childCount callee call &&
          symbolPrecedes call.target.body caller program.calleeBeforeCaller
  else true

def nodeLocalCallSiteValid (program : Program) (caller : String) (node : Node) : Bool :=
  descriptorShapeValid node.kind node.children.length node.call &&
    match node.call with
    | none => true
    | some call => localCallSiteValid program caller node.children.length call

def nodeLocalCallClosureValidFuel (program : Program) (caller : String) : Nat → Node → Bool
  | 0, _ => false
  | fuel + 1, node =>
      nodeLocalCallSiteValid program caller node &&
        node.children.all (nodeLocalCallClosureValidFuel program caller fuel)

def nodeLocalCallClosureValid (program : Program) (caller : String) (node : Node) : Bool :=
  nodeLocalCallClosureValidFuel program caller 2048 node

def programLocalCallClosureValid (program : Program) : Bool :=
  calleeOrderShapeValid program &&
    program.functions.all (fun function => function.blocks.all (fun block =>
      block.operations.all (nodeLocalCallClosureValid program function.signature.symbol) &&
        block.branchValue.all (nodeLocalCallClosureValid program function.signature.symbol)))

def ProgramLocalCallClosure (program : Program) : Prop :=
  programLocalCallClosureValid program = true

theorem nodeLocalCallClosureValidFuel_site (program : Program) (caller : String)
    (fuel : Nat) (node : Node)
    (valid : nodeLocalCallClosureValidFuel program caller (fuel + 1) node = true) :
    nodeLocalCallSiteValid program caller node = true := by
  simp only [nodeLocalCallClosureValidFuel, Bool.and_eq_true] at valid
  exact valid.1

theorem nodeLocalCallClosureValidFuel_child (program : Program) (caller : String)
    (fuel : Nat) (node child : Node)
    (valid : nodeLocalCallClosureValidFuel program caller (fuel + 1) node = true)
    (member : child ∈ node.children) :
    nodeLocalCallClosureValidFuel program caller fuel child = true := by
  simp only [nodeLocalCallClosureValidFuel, Bool.and_eq_true] at valid
  exact List.all_eq_true.mp valid.2 child member

theorem nodeLocalCallSiteValid_descriptor (program : Program) (caller : String) (node : Node)
    (valid : nodeLocalCallSiteValid program caller node = true) :
    descriptorShapeValid node.kind node.children.length node.call = true := by
  simp only [nodeLocalCallSiteValid, Bool.and_eq_true] at valid
  exact valid.1

theorem nodeLocalCallSiteValid_local (program : Program) (caller : String) (node : Node)
    (call : Call) (hasCall : node.call = some call)
    (valid : nodeLocalCallSiteValid program caller node = true) :
    localCallSiteValid program caller node.children.length call = true := by
  simp only [nodeLocalCallSiteValid, Bool.and_eq_true] at valid
  rw [hasCall] at valid
  exact valid.2

theorem localCallSiteValid_components (program : Program) (caller : String)
    (childCount : Nat) (call : Call) (isLocal : call.target.kind = .local)
    (valid : localCallSiteValid program caller childCount call = true) :
    ∃ callee ∈ program.functions,
      callee.signature.symbol = call.target.body ∧
      receiverChildValid childCount call.receiverChild = true ∧
      localParameterShapeValid childCount callee call = true ∧
      symbolPrecedes call.target.body caller program.calleeBeforeCaller = true := by
  cases found : program.functions.find? (fun function =>
      function.signature.symbol == call.target.body) with
  | none =>
      simp [localCallSiteValid, isLocal, found] at valid
  | some callee =>
      have member := List.mem_of_find?_eq_some found
      have symbol : callee.signature.symbol = call.target.body := by
        simpa using List.find?_some found
      refine ⟨callee, member, symbol, ?_⟩
      simp [localCallSiteValid, isLocal, found, Bool.and_eq_true] at valid
      exact ⟨valid.1.1, valid.1.2, valid.2⟩

theorem localParameterShapeValid_descriptorFree (childCount : Nat) (callee : Function)
    (call : Call) (descriptorFree : call.arguments = [])
    (valid : localParameterShapeValid childCount callee call = true) :
    callee.signature.parameters.length = call.target.member.parameters.length ∧
      (callee.signature.parameters = [] ∨
        callee.signature.parameters.length = childCount) := by
  simp [localParameterShapeValid, descriptorFree, Bool.and_eq_true] at valid
  exact valid

theorem localParameterShapeValid_explicit (childCount : Nat) (callee : Function)
    (call : Call) (explicit : call.arguments ≠ [])
    (valid : localParameterShapeValid childCount callee call = true) :
    callee.signature.parameters.length = call.target.member.parameters.length ∧
      call.arguments.length = callee.signature.parameters.length ∧
      argumentReferencesValid childCount callee.signature.parameters.length call.arguments = true ∧
      argumentOrdinalsUnique call.arguments = true := by
  simp [localParameterShapeValid, explicit, Bool.and_eq_true] at valid
  exact ⟨valid.1, valid.2.1.1, valid.2.1.2, valid.2.2⟩

def localConstructorPlanValid (site : Option Node) (callee : Function)
    (arguments : List Argument) (symbols : List String) : Bool :=
  site.any (fun node => node.call.any (fun call =>
    match call.arguments.mapM (fun argument => callee.signature.parameters[argument.ordinal]?) with
    | none => false
    | some parameters =>
        node.kind == .objectCreation && node.mode == .value &&
          node.symbol == callee.signature.symbol &&
          callee.signature.kind == .constructor && callee.signature.receiver == .value &&
          call.target.kind == .local && call.target.body == callee.signature.symbol &&
          call.target.member.symbol == callee.signature.symbol &&
          call.target.member.kind == .constructor && call.target.member.receiver == .value &&
          call.receiverChild == -1 && call.arguments == arguments &&
          call.arguments.all (fun argument => argument.mode == .value) &&
          !call.arguments.isEmpty && localParameterShapeValid node.children.length callee call &&
          parameters.map (·.symbol) == symbols &&
          parameters.all (fun parameter => parameter.refKind == .none) &&
          decide (parameters.map (·.symbol)).Nodup))

theorem localConstructorPlanValid_components (site : Option Node) (callee : Function)
    (arguments : List Argument) (symbols : List String)
    (valid : localConstructorPlanValid site callee arguments symbols = true) :
    ∃ node call parameters, site = some node ∧ node.call = some call ∧
      call.arguments.mapM (fun argument => callee.signature.parameters[argument.ordinal]?) = some parameters ∧
      node.kind = .objectCreation ∧ node.mode = .value ∧ node.symbol = callee.signature.symbol ∧
      callee.signature.kind = .constructor ∧ callee.signature.receiver = .value ∧
      call.target.kind = .local ∧ call.target.body = callee.signature.symbol ∧
      call.target.member.symbol = callee.signature.symbol ∧
      call.target.member.kind = .constructor ∧ call.target.member.receiver = .value ∧
      call.receiverChild = -1 ∧
      call.arguments = arguments ∧ (∀ argument ∈ call.arguments, argument.mode = .value) ∧
      call.arguments ≠ [] ∧ localParameterShapeValid node.children.length callee call = true ∧
      parameters.map (·.symbol) = symbols ∧
      (∀ parameter ∈ parameters, parameter.refKind = .none) ∧
      (parameters.map (·.symbol)).Nodup := by
  cases site with
  | none => simp [localConstructorPlanValid] at valid
  | some node =>
      cases found : node.call with
      | none => simp [localConstructorPlanValid, found] at valid
      | some call =>
          cases selected : call.arguments.mapM
              (fun argument => callee.signature.parameters[argument.ordinal]?) with
          | none => simp [localConstructorPlanValid, found, selected] at valid
          | some parameters =>
              refine ⟨node, call, parameters, rfl, found, selected, ?_⟩
              simpa [localConstructorPlanValid, found, selected, Bool.and_eq_true, and_assoc] using valid

theorem localConstructorPlanValid_bindingPremises (site : Option Node) (callee : Function)
    (arguments : List Argument) (symbols : List String)
    (valid : localConstructorPlanValid site callee arguments symbols = true)
    (node : Node) (atSite : site = some node) (call : Call) (hasCall : node.call = some call) :
    ∃ parameters, call.arguments ≠ [] ∧
      call.arguments.mapM (fun argument => callee.signature.parameters[argument.ordinal]?) = some parameters ∧
      parameters.map (·.symbol) = symbols ∧
      (∀ parameter ∈ parameters, parameter.refKind = .none) ∧
      (parameters.map (·.symbol)).Nodup := by
  obtain ⟨selectedNode, selectedCall, parameters, selectedSite, selectedDescriptor, selected,
    _, _, _, _, _, _, _, _, _, _, _, _, _, explicit, _, order, byValue, unique⟩ :=
      localConstructorPlanValid_components site callee arguments symbols valid
  have sameNode : selectedNode = node := Option.some.inj (selectedSite.symm.trans atSite)
  subst selectedNode
  have sameCall : selectedCall = call := Option.some.inj (selectedDescriptor.symm.trans hasCall)
  subst selectedCall
  exact ⟨parameters, explicit, selected, order, byValue, unique⟩

def generatedTransactionResultConstructorSite : Option Node := do
  let block ← Generated.function8.blocks[0]?
  block.branchValue

def generatedTransactionSubstateConstructorSite : Option Node := do
  let block ← Generated.function24.blocks[27]?
  let operation ← block.operations[0]?
  let assignment ← operation.children[1]?
  assignment.children[0]?

def generatedTransactionResultConstructorSymbols : List String :=
  ["::parameter:0:error:None:global::Nethermind.Evm.TransactionProcessing.TransactionResult.ErrorType",
    "::parameter:2:errorDescription:None:string",
    "::parameter:1:evmException:None:global::Nethermind.Evm.EvmExceptionType"].map
      (Generated.function7.signature.symbol ++ ·)

def generatedTransactionSubstateConstructorSymbols : List String :=
  ["::parameter:0:bytes:None:global::System.ReadOnlyMemory<byte>",
    "::parameter:1:refund:None:long",
    "::parameter:2:destroyList:None:global::Nethermind.Core.Collections.JournalSet<global::Nethermind.Core.Address>",
    "::parameter:3:logs:None:global::Nethermind.Core.Collections.JournalCollection<global::Nethermind.Core.LogEntry>",
    "::parameter:4:shouldRevert:None:bool",
    "::parameter:5:isTracerConnected:None:bool",
    "::parameter:6:evmExceptionType:None:global::Nethermind.Evm.EvmExceptionType",
    "::parameter:7:logger:None:global::Nethermind.Logging.ILogger"].map
      (Generated.function22.signature.symbol ++ ·)

set_option maxRecDepth 10000 in
set_option maxHeartbeats 8000000 in
theorem generated_transactionResult_constructorPlan :
    localConstructorPlanValid generatedTransactionResultConstructorSite Generated.function7
      [{ child := 0, ordinal := 0, mode := .value, implicit := false, kind := "Explicit" },
        { child := 1, ordinal := 2, mode := .value, implicit := false, kind := "Explicit" },
        { child := 2, ordinal := 1, mode := .value, implicit := true, kind := "DefaultValue" }]
      generatedTransactionResultConstructorSymbols = true := by
  decide

set_option maxRecDepth 10000 in
set_option maxHeartbeats 8000000 in
theorem generated_transactionSubstate_constructorPlan :
    localConstructorPlanValid generatedTransactionSubstateConstructorSite Generated.function22
      ((List.range 8).map (fun ordinal =>
        { child := ordinal, ordinal, mode := .value, implicit := false, kind := "Explicit" }))
      generatedTransactionSubstateConstructorSymbols = true := by
  decide

theorem programLocalCallClosureValid_order (program : Program)
    (valid : programLocalCallClosureValid program = true) :
    calleeOrderShapeValid program = true := by
  simp only [programLocalCallClosureValid, Bool.and_eq_true] at valid
  exact valid.1

theorem programLocalCallClosureValid_operation (program : Program)
    (valid : programLocalCallClosureValid program = true)
    (function : Function) (functionMember : function ∈ program.functions)
    (block : Block) (blockMember : block ∈ function.blocks)
    (node : Node) (nodeMember : node ∈ block.operations) :
    nodeLocalCallClosureValid program function.signature.symbol node = true := by
  simp only [programLocalCallClosureValid, Bool.and_eq_true] at valid
  have functionValid := List.all_eq_true.mp valid.2 function functionMember
  have blockValid := List.all_eq_true.mp functionValid block blockMember
  simp only [Bool.and_eq_true] at blockValid
  exact List.all_eq_true.mp blockValid.1 node nodeMember

theorem programLocalCallClosureValid_branch (program : Program)
    (valid : programLocalCallClosureValid program = true)
    (function : Function) (functionMember : function ∈ program.functions)
    (block : Block) (blockMember : block ∈ function.blocks)
    (node : Node) (branch : block.branchValue = some node) :
    nodeLocalCallClosureValid program function.signature.symbol node = true := by
  simp only [programLocalCallClosureValid, Bool.and_eq_true] at valid
  have functionValid := List.all_eq_true.mp valid.2 function functionMember
  have blockValid := List.all_eq_true.mp functionValid block blockMember
  simp only [Bool.and_eq_true] at blockValid
  simpa [branch] using blockValid.2

def nodeCountFuel : Nat → Node → Nat
  | 0, _ => 0
  | fuel + 1, node =>
      1 + (node.children.map (nodeCountFuel fuel)).sum

def localCallCountFuel : Nat → Node → Nat
  | 0, _ => 0
  | fuel + 1, node =>
      (if node.call.any (fun call => call.target.kind == .local) then 1 else 0) +
        (node.children.map (localCallCountFuel fuel)).sum

def descriptorNodeCountFuel : Nat → Node → Nat
  | 0, _ => 0
  | fuel + 1, node =>
      (if node.kind == .invocation || node.kind == .objectCreation then 1 else 0) +
        (node.children.map (descriptorNodeCountFuel fuel)).sum

private def programNodeSum (program : Program) (measure : Node → Nat) : Nat :=
  (program.functions.map (fun function =>
    (function.blocks.map (fun block =>
      (block.operations.map measure).sum + (block.branchValue.map measure).getD 0)).sum)).sum

def programNodeCount (program : Program) : Nat :=
  programNodeSum program (nodeCountFuel 2048)

def programLocalCallCount (program : Program) : Nat :=
  programNodeSum program (localCallCountFuel 2048)

def programDescriptorNodeCount (program : Program) : Nat :=
  programNodeSum program (descriptorNodeCountFuel 2048)

set_option maxRecDepth 10000 in
set_option maxHeartbeats 8000000 in
theorem generated_program_localCallClosure : ProgramLocalCallClosure Generated.program := by
  unfold ProgramLocalCallClosure
  decide

set_option maxRecDepth 10000 in
set_option maxHeartbeats 8000000 in
theorem generated_program_nodeCount : programNodeCount Generated.program = 768 := by
  decide

set_option maxRecDepth 10000 in
set_option maxHeartbeats 8000000 in
theorem generated_program_localCallCount : programLocalCallCount Generated.program = 23 := by
  decide

set_option maxRecDepth 10000 in
set_option maxHeartbeats 8000000 in
theorem generated_program_descriptorNodeCount :
    programDescriptorNodeCount Generated.program = 47 := by
  decide

example : symbolPrecedes "callee" "caller" ["callee", "caller"] = true := by
  decide

example : symbolPrecedes "callee" "caller" ["caller", "callee"] = false := by
  decide

example : receiverChildValid 0 (-1) = true := by
  decide

example : receiverChildValid 0 0 = false := by
  decide

example (argument : Argument) :
    argumentReferencesValid 1 1 [{ argument with child := 1, ordinal := 0 }] = false := by
  simp [argumentReferencesValid]

example (argument : Argument) :
    argumentReferencesValid 1 1 [{ argument with child := 0, ordinal := 1 }] = false := by
  simp [argumentReferencesValid]

example (argument : Argument) :
    argumentOrdinalsUnique [argument, argument] = false := by
  simp [argumentOrdinalsUnique]

example (function : Function) (call : Call) (parameter : Parameter) :
    localParameterShapeValid 0
      { function with signature := { function.signature with parameters := [] } }
      { call with
        target := { call.target with
          member := { call.target.member with parameters := [parameter] } }
        arguments := [] } = false := by
  simp [localParameterShapeValid]

example (function : Function) (call : Call) (parameter : Parameter) :
    localParameterShapeValid 0
      { function with
        signature := { function.signature with parameters := [parameter] } }
      { call with
        target := { call.target with
          member := { call.target.member with parameters := [parameter] } }
        arguments := [] } = false := by
  simp [localParameterShapeValid]

example (function : Function) (call : Call) :
    localParameterShapeValid 3
      { function with signature := { function.signature with parameters := [] } }
      { call with
        target := { call.target with
          member := { call.target.member with parameters := [] } }
        arguments := [] } = true := by
  simp [localParameterShapeValid]

example : descriptorShapeValid .invocation 0 none = false := by
  rfl

example : descriptorShapeValid .objectCreation 0 none = false := by
  rfl

example (call : Call) (argument : Argument) :
    descriptorShapeValid .invocation 1
      (some { call with
        target := { call.target with
          member := { call.target.member with parameters := [] } }
        arguments := [{ argument with child := 0, ordinal := 0 }] }) = false := by
  simp [descriptorShapeValid]

example (program : Program) (caller : String) (call : Call) :
    localCallSiteValid { program with functions := [] } caller 0
      { call with target := { call.target with kind := .local } } = false := by
  simp [localCallSiteValid]

example (program : Program) :
    calleeOrderShapeValid { program with calleeBeforeCaller := ["duplicate", "duplicate"] } = false := by
  simp [calleeOrderShapeValid]

example (program : Program) (function : Function) :
    calleeOrderShapeValid { program with
      functions := [function]
      calleeBeforeCaller := [] } = false := by
  simp [calleeOrderShapeValid]

set_option maxRecDepth 10000 in
set_option maxHeartbeats 8000000 in
theorem generated_program_returnFree : ProgramReturnFree Generated.program := by
  apply programValid_returnFree
  decide

end SimpleTransferCompletionExtractor.StageB.Runtime
