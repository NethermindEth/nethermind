-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

namespace SimpleTransferCompletionExtractor.StageB

inductive OperationKind where
  | declarationExpression | expressionStatement | returnValue | assignment | invocation | objectCreation
  | collection | field | property | local | parameter | instance | literal | defaultValue | binary | unary
  | conversion | parenthesized | argument | isPattern | constantPattern | negatedPattern | isNull
  | capture | captureReference | discard

inductive OperandMode where | value | location | readOnlyLocation deriving DecidableEq, Repr
inductive RefKind where | none | ref | out | inRef | refReadOnly | refReadOnlyParameter deriving DecidableEq, Repr
inductive ArgumentMode where | value | readOnlyLocation | readOnlyTemporary | writableLocation | outLocation deriving DecidableEq, Repr
inductive TargetKind where | local | staticField | defaultValue | initialization | stateCharge | external | refund deriving DecidableEq, Repr
inductive RequestKind where | constant | projection | worldState | tracer | codeLookup | pool | collection | arithmetic | representation | framework deriving DecidableEq, Repr
inductive ExitKind where | branch | returnValue | suspend | outside deriving DecidableEq, Repr
inductive ConditionKind where | none | whenTrue | whenFalse deriving DecidableEq, Repr

inductive Primitive where
  | unsupported
  | txSender | txRecipient | txValue | txValueRef | txData | txGasLimit | txAuthorizationList
  | specEip8037 | specEip7708 | tracerState | tracerActions | tracerCode | tracerLogs | tracerAccess
  | processorWorld | processorCodeRepository | processorCodeOverridable | processorLogger
  | forceSimpleTransferDisabled | defaultTxGasLimitCap | newAccountState | uint256Zero | nullTracer
  | structField | uint256IsZero | codeInfoIsEmpty | addressEquality | boolNot
  | codeLookup | isDeadAccount | subtractBalance | addBalance | commit | trace | enumHasFlag | uint256Min
  | journalConstructor
  deriving DecidableEq, Repr

inductive Literal where
  | none
  | null
  | boolean (value : Bool)
  | unsigned (value : Nat)
  | signed (value : Int)
  | text (value : String)
  deriving DecidableEq, Repr

structure Binding where
  capture : Int
  refKind : RefKind
  isRef : Bool
  argumentMode : ArgumentMode
  callTarget : String
  callReceiverChild : Int
  callArgumentChildren : List Nat
  hasCall : Bool
  additionalMembers : List String
  deriving DecidableEq, Repr

structure Parameter where
  symbol : String
  name : String
  ordinal : Nat
  typeName : String
  refKind : RefKind
  optional : Bool
  deriving DecidableEq, Repr

inductive MemberKind where | method | constructor | property | field deriving DecidableEq, Repr
inductive ReceiverKind where | static | reference | value deriving DecidableEq, Repr

structure Member where
  symbol : String
  definition : String
  name : String
  declaringType : String
  kind : MemberKind
  receiver : ReceiverKind
  typeName : String
  refKind : RefKind
  parameters : List Parameter
  deriving DecidableEq, Repr

structure Argument where
  child : Nat
  ordinal : Nat
  mode : ArgumentMode
  implicit : Bool
  kind : String
  deriving DecidableEq, Repr

structure Target where
  kind : TargetKind
  member : Member
  body : String
  requestKind : Option RequestKind
  primitive : Primitive
  fieldName : String
  deriving DecidableEq, Repr

structure Call where
  target : Target
  receiverChild : Int
  arguments : List Argument
  deriving DecidableEq, Repr

inductive Node where
  | mk
      (kind : OperationKind)
      (typeName symbol operator constant conversion : String)
      (literal : Literal)
      (primitive : Primitive)
      (implicit completes : Bool)
      (mode : OperandMode)
      (binding : Binding)
      (call : Option Call)
      (additionalTargets : List Target)
      (children : List Node)

namespace Node
def kind : Node -> OperationKind | .mk kind .. => kind
def typeName : Node -> String | .mk _ typeName .. => typeName
def symbol : Node -> String | .mk _ _ symbol .. => symbol
def operator : Node -> String | .mk _ _ _ operator .. => operator
def constant : Node -> String | .mk _ _ _ _ constant .. => constant
def conversion : Node -> String | .mk _ _ _ _ _ conversion .. => conversion
def literal : Node -> Literal | .mk _ _ _ _ _ _ literal .. => literal
def primitive : Node -> Primitive | .mk _ _ _ _ _ _ _ primitive .. => primitive
def implicit : Node -> Bool | .mk _ _ _ _ _ _ _ _ implicit .. => implicit
def completes : Node -> Bool | .mk _ _ _ _ _ _ _ _ _ completes .. => completes
def mode : Node -> OperandMode | .mk _ _ _ _ _ _ _ _ _ _ mode .. => mode
def binding : Node -> Binding | .mk _ _ _ _ _ _ _ _ _ _ _ binding .. => binding
def call : Node -> Option Call | .mk _ _ _ _ _ _ _ _ _ _ _ _ call .. => call
def additionalTargets : Node -> List Target | .mk _ _ _ _ _ _ _ _ _ _ _ _ _ additionalTargets .. => additionalTargets
def children : Node -> List Node | .mk _ _ _ _ _ _ _ _ _ _ _ _ _ _ children => children
end Node

structure Edge where
  destination : Int
  semantics : String
  leavingRegions : List Nat
  enteringRegions : List Nat
  finallyRegions : List Nat
  deriving DecidableEq, Repr

structure Region where
  id : Int
  parent : Int
  kind : String
  firstBlock : Int
  lastBlock : Int
  locals : List String
  captures : List Int
  exceptionType : String
  deriving DecidableEq, Repr

structure CaptureInfo where
  id : Int
  region : Int
  typeName : String
  deriving DecidableEq, Repr

structure BlockBound where
  block : Nat
  fuel : Nat
  deriving DecidableEq, Repr

structure Block where
  ordinal : Nat
  operations : List Node
  branchValue : Option Node
  condition : ConditionKind
  exit : ExitKind
  fallThrough : Option Edge
  conditional : Option Edge

structure Function where
  signature : Member
  entryBlock : Nat
  entryOperationProvenance : Nat
  bindings : List (String × String × RefKind × Int)
  captures : List CaptureInfo
  regions : List Region
  entryFacts : List String
  captureModes : List (Int × OperandMode)
  blocks : List Block
  calls : List String
  blockBounds : List BlockBound
  fuelBound : Nat
  mayReturn : Bool

structure SourcePin where
  path : String
  role : String
  sha256 : String
  deriving DecidableEq, Repr

structure ReferencePin where
  path : String
  assemblyName : String
  sha256 : String
  mvid : String
  selected : Bool
  deriving DecidableEq, Repr

structure StageDependency where
  symbol : String
  path : String
  sourceSha256 : String
  deriving DecidableEq, Repr

structure StagePin where
  name : String
  theoremName : String
  entryPoints : List String
  sourceClosure : List StageDependency
  closureSha256 : String
  deriving DecidableEq, Repr

inductive TypeKind where | void | value | reference | array | parameter deriving DecidableEq, Repr
structure FieldLayout where
  symbol : String
  typeName : String
  readOnly : Bool
  associatedProperty : String
  deriving DecidableEq, Repr
structure TypeInfo where
  symbol : String
  kind : TypeKind
  elementType : String
  typeArguments : List String
  fields : List FieldLayout

inductive SourceTerm where
  | mk
      (kind : String) (typeName symbol operator constant argumentKind refKind conversion : String)
      (implicit : Bool) (parameterOrdinal : Int) (binding : Option Binding) (children : List SourceTerm)

structure SourceBlock where
  ordinal : Nat
  kind : String
  reachable : Bool
  conditionKind : String
  operations : List SourceTerm
  branchValue : Option SourceTerm
  containsExcludedOperations : Bool
  fallThrough : Option Edge
  conditional : Option Edge

structure Continuation where
  functionSymbol : String
  block : Nat
  operation : Nat
  callPath : List Nat
  pendingOperation : SourceTerm
  sourceBlocks : List SourceBlock

structure RefundSuspension where
  call : Call
  orderedOperands : List Node
  continuation : Continuation

structure Program where
  schemaVersion : Nat
  entry : String
  policyType : String
  scope : String
  prefixIntegrity : String
  compilerSources : List SourcePin
  compilerReferences : List ReferencePin
  externalPremises : List String
  acceptedStages : List StagePin
  functions : List Function
  initializers : List String
  members : List Member
  types : List TypeInfo
  refund : RefundSuspension
  calleeBeforeCaller : List String
  fuelBound : Nat

inductive Value where
  | unit | null (typeName : String) | boolean (value : Bool) | unsigned (value : Nat) | signed (value : Int)
  | uint256 (value : Nat) | enum (typeName : String) (value : Int) | address (identity : String)
  | bytes (value : List UInt8) | reference (typeName identity : String) | struct (typeName : String) (fields : List (String × Value))

structure Gas where
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

structure Intrinsic where
  standard : Gas
  floorGas : Gas
  deriving DecidableEq, Repr

structure Transaction where
  sender : String
  recipient : String
  value : Nat
  data : List UInt8
  gasLimit : Nat
  deriving DecidableEq, Repr
structure Input where
  tx : Transaction
  intrinsic : Intrinsic
  opcodeGasPrice : Nat
  premiumPerGas : Nat
  senderReservedGasPayment : Nat
  blobBaseFee : Nat
  eip8037Enabled : Bool
  deriving DecidableEq, Repr

inductive Request where
  | codeLookup (recipient : String) (followDelegation : Bool)
  | incrementEmptyCalls
  | isDeadAccount (recipient : String)
  | subtractBalance (address : String) (amount : Nat)
  | addBalance (address : String) (amount : Nat)
  deriving DecidableEq, Repr

inductive Reply where
  | codeInfo (isEmpty : Bool) (delegation : Option String)
  | boolean (value : Bool)
  | unit
  deriving DecidableEq, Repr

structure Exchange where
  request : Request
  reply : Reply
  deriving DecidableEq, Repr
inductive PassingMode where | value | readOnlyLocation deriving DecidableEq, Repr

structure RefundOperand where
  ordinal : Nat
  mode : PassingMode
  value : Value
  provenance : Option String

structure Suspension where
  operands : List RefundOperand
  requests : List Request
  remainingTape : List Exchange
  remainingCSharpFuel : Nat

inductive Outcome where
  | suspended (value : Suspension)
  | rejected (reason : String) (requests : List Request) (remainingTape : List Exchange)
  | fuelExhausted (requests : List Request) (remainingTape : List Exchange)
  | microFuelExhausted (requests : List Request) (remainingTape : List Exchange)

end SimpleTransferCompletionExtractor.StageB
