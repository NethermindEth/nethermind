-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Generated from the exact Stage E source/member admission profile.
-- This theorem-free kernel describes one source-attached ExecuteTransaction
-- frame-driver algebra step. It never calls itself recursively and therefore
-- does not claim a whole run or a production C# refinement.
-- Bytecode, full-precompile, journal, gas/refund, and settlement behavior remain
-- explicit accepted leaf adapters.
-- Canonical IR SHA-256: 77acc12d000498ea3523b9df45e8e00f78bfcabbcdcdd8060b64ae55790be1e8

import EvmFrameMachineExtractor.Specification.FrameMachineState
import EvmFrameDriverExtractor.Specification.Types

namespace Eip803x.Evm.FrameDriver.Generated

open FrameMachineState
def irSha256 : String := "77acc12d000498ea3523b9df45e8e00f78bfcabbcdcdd8060b64ae55790be1e8"

def admittedBranchNames : List String := [
  "FreshPreparation",
  "ContinuationPreparation",
  "BytecodeDispatch",
  "FullPrecompileDispatch",
  "BytecodeContinue",
  "BytecodeSuspend",
  "NestedRegularSuccess",
  "NestedCreateSuccess",
  "NestedCreateInvalidCode",
  "NestedCreateOutOfGas",
  "NestedRevert",
  "NestedException",
  "TopLevelSuccess",
  "TopLevelRevert",
  "TopLevelException",
  "FullPrecompileOutOfGasNested",
  "FullPrecompileOutOfGasTop",
  "FullPrecompileReturnedFailure",
  "FullPrecompileManagedException",
  "Cancelled",
  "Escaped",
  "InvalidControl",
]

def admittedBranchContracts : List String := [
  "FreshPreparation|fresh|clearReturnData -> prepareFresh|preparation",
  "ContinuationPreparation|continuation|retainReturnData -> prepareContinuation|preparation",
  "BytecodeDispatch|bytecode|RunByteCode -> RunDispatchLoop|invocation",
  "FullPrecompileDispatch|fullPrecompile|RunPrecompile -> ExecutePrecompile|invocation",
  "BytecodeContinue|returned/continue|retainCurrentFrame|continued",
  "BytecodeSuspend|returned/suspend|prepareChildFrame -> retainParent|suspended",
  "NestedRegularSuccess|halt/success/nested/call|popParent -> mergeChild -> repayStateGasSpill|childSuccess",
  "NestedCreateSuccess|halt/success/nested/create|prepareCreateData -> HandleCreate|childCreateSuccess",
  "NestedCreateInvalidCode|createDeposit/invalid|restoreWorld -> creditParent|childCreateInvalidCode",
  "NestedCreateOutOfGas|createDeposit/outOfGas|burnDepositGas -> restoreWorld|childCreateOutOfGas",
  "NestedRevert|halt/revert/nested|restoreSnapshot -> restoreChildGas -> HandleRevert|childRevert",
  "NestedException|halt/exception/nested|restoreFailureControl -> resumeParent|childException",
  "TopLevelSuccess|halt/success/top|PrepareTopLevelSubstate|topLevelSuccess",
  "TopLevelRevert|halt/revert/top|refundRevertedStateGas|topLevelRevert",
  "TopLevelException|halt/exception/top|HandleExceptionOrFailure|topLevelException",
  "FullPrecompileOutOfGasNested|precompile/outOfGas/nested|failureSettlement|fullPrecompileOutOfGasNested",
  "FullPrecompileOutOfGasTop|precompile/outOfGas/top|failureSettlement|fullPrecompileOutOfGasTop",
  "FullPrecompileReturnedFailure|precompile/returnedFailure|failureSettlement|fullPrecompileReturnedFailure",
  "FullPrecompileManagedException|precompile/managedException|failureSettlement|fullPrecompileManagedException",
  "Cancelled|bytecode/cancelled|FrameCleanupScope.Dispose|cancelled",
  "Escaped|callback/escaped|FrameCleanupScope.Dispose|escaped",
  "InvalidControl|invalidControl|failClosed|invalidControl",
]

def admittedDispatchOrder : List String := [
  "clearReturnDataOnFreshOnly",
  "prepareFreshOrContinuation",
  "selectBytecodeOrFullPrecompile",
  "invokeRunByteCodeOrRunPrecompile",
  "classifyContinueSuspendOrReturnedChild",
  "classifyTopLevelOrNestedReturn",
  "classifyNestedCreateCodeDeposit",
  "invokeAcceptedSettlementLeaf",
  "cleanupCancellationEscapeOrInvalidControl",
]

def admittedSourceControl : SourceControl :=
  { admittedMembers := [
  "ExecuteTransaction",
  "RunByteCode",
  "RunDispatchLoop",
  "Dispose",
]
    admittedOperations := [
  "ExecuteTransaction:call:Validate",
  "ExecuteTransaction:call:PrepareOpcodes",
  "ExecuteTransaction:using",
  "ExecuteTransaction:while",
  "ExecuteTransaction:if",
  "ExecuteTransaction:try",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:ExecutePrecompile",
  "ExecuteTransaction:if",
  "ExecuteTransaction:goto",
  "ExecuteTransaction:if",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:TraceTransactionActionStart",
  "ExecuteTransaction:call:AddTransferLog",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:ExecuteCall",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:PrepareNextCallFrame",
  "ExecuteTransaction:continue",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:HandleException",
  "ExecuteTransaction:if",
  "ExecuteTransaction:return",
  "ExecuteTransaction:continue",
  "ExecuteTransaction:if",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:TraceTransactionActionEnd",
  "ExecuteTransaction:call:PrepareTopLevelSubstate",
  "ExecuteTransaction:return",
  "ExecuteTransaction:using",
  "ExecuteTransaction:call:Pop",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:IsAnyCreate",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:IncorporateChildStateGasRefunds",
  "ExecuteTransaction:call:Refund",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:GetRemainingGas",
  "ExecuteTransaction:call:PrepareCreateData",
  "ExecuteTransaction:call:HandleCreate",
  "ExecuteTransaction:call:HandleRegularReturn",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:CommitToParent",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:IncorporateChildStateGasRefunds",
  "ExecuteTransaction:call:RepayStateGasSpill",
  "ExecuteTransaction:call:UpdateGasUp",
  "ExecuteTransaction:call:GetRemainingGas",
  "ExecuteTransaction:call:RemoveAdvancedStateGasRefund",
  "ExecuteTransaction:call:RestoreChildStateGas",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:IsAnyCreate",
  "ExecuteTransaction:call:CreditStateGasRefund",
  "ExecuteTransaction:call:GetCreateStateCost",
  "ExecuteTransaction:if",
  "ExecuteTransaction:call:CreditStateGasRefund",
  "ExecuteTransaction:call:GetNewAccountStateCost",
  "ExecuteTransaction:call:HandleRevert",
  "ExecuteTransaction:catch",
  "ExecuteTransaction:goto",
  "ExecuteTransaction:continue",
  "ExecuteTransaction:label",
  "ExecuteTransaction:call:HandleFailure",
  "ExecuteTransaction:if",
  "ExecuteTransaction:return",
  "RunByteCode:call:RunDispatchLoop",
  "RunByteCode:if",
  "RunByteCode:if",
  "RunByteCode:call:IsAnyCreate",
  "RunByteCode:call:EndInstructionTrace",
  "RunByteCode:call:GetRemainingGas",
  "RunByteCode:goto",
  "RunByteCode:call:Assert",
  "RunByteCode:if",
  "RunByteCode:return",
  "RunByteCode:call:As",
  "RunByteCode:if",
  "RunByteCode:goto",
  "RunByteCode:if",
  "RunByteCode:goto",
  "RunByteCode:return",
  "RunByteCode:call:Empty",
  "RunByteCode:label",
  "RunByteCode:call:Assert",
  "RunByteCode:return",
  "RunByteCode:call:As",
  "RunByteCode:label",
  "RunByteCode:call:Assert",
  "RunByteCode:return",
  "RunByteCode:call:As",
  "RunByteCode:label",
  "RunByteCode:if",
  "RunByteCode:call:ClearExecutionGas",
  "RunByteCode:return",
  "RunByteCode:call:GetFailureReturn",
  "RunByteCode:call:GetRemainingGas",
  "RunDispatchLoop:if",
  "RunDispatchLoop:return",
  "RunDispatchLoop:if",
  "RunDispatchLoop:call:Add",
  "RunDispatchLoop:return",
  "RunDispatchLoop:if",
  "RunDispatchLoop:call:ThrowOperationCanceledException",
  "RunDispatchLoop:while",
  "RunDispatchLoop:call:Add",
  "RunDispatchLoop:if",
  "RunDispatchLoop:break",
  "RunDispatchLoop:if",
  "RunDispatchLoop:call:ThrowOperationCanceledException",
  "RunDispatchLoop:return",
  "Dispose:if",
  "Dispose:call:DisposeActiveFrames",
] }

def admittedControlNodes : List ControlNodeEvidence := [
  { member := "ExecuteTransaction", id := "n000", kind := "statement", parentId := "root", arm := "body", condition := "-", sha256 := "e746ca27a0401f49eb875b0e0e3897b632e9c927a86dce03e2da926545a370ae", operations := ["call:Validate", ] },
  { member := "ExecuteTransaction", id := "n001", kind := "statement", parentId := "root", arm := "body", condition := "-", sha256 := "82765b6590f1d16c5de7a30fbd849c6d5b1d19fbd353ea921d3874215f4a47a5", operations := ["call:PrepareOpcodes", ] },
  { member := "ExecuteTransaction", id := "n002", kind := "using", parentId := "root", arm := "body", condition := "FrameCleanupScope_=new(this,vmState)", sha256 := "2984d36bafc29869995a24f7328eb6f03c2f5f94d656b65a7a49316f525c4f33", operations := [] },
  { member := "ExecuteTransaction", id := "n003", kind := "statement", parentId := "root", arm := "body", condition := "-", sha256 := "ae37d3ae148af668bd01338f17660cecf542b81e7a2a8d485871bba65ab84c6b", operations := [] },
  { member := "ExecuteTransaction", id := "n004", kind := "while", parentId := "root", arm := "body", condition := "true", sha256 := "6b12ad4388e33ef4ac8cef5f40f07429eb1e626dc3465a0d6eab4a2c4407f753", operations := ["call:ExecutePrecompile", "call:TraceTransactionActionStart", "call:AddTransferLog", "call:ExecuteCall", "call:PrepareNextCallFrame", "call:HandleException", "call:TraceTransactionActionEnd", "call:PrepareTopLevelSubstate", "call:Pop", "call:IsAnyCreate", "call:IncorporateChildStateGasRefunds", "call:Refund", "call:GetRemainingGas", "call:PrepareCreateData", "call:HandleCreate", "call:HandleRegularReturn", "call:CommitToParent", "call:IncorporateChildStateGasRefunds", "call:RepayStateGasSpill", "call:UpdateGasUp", "call:GetRemainingGas", "call:RemoveAdvancedStateGasRefund", "call:RestoreChildStateGas", "call:IsAnyCreate", "call:CreditStateGasRefund", "call:GetCreateStateCost", "call:CreditStateGasRefund", "call:GetNewAccountStateCost", "call:HandleRevert", "call:HandleFailure", ] },
  { member := "ExecuteTransaction", id := "n005", kind := "if", parentId := "n004", arm := "body", condition := "!_currentState.IsContinuation", sha256 := "dad25b4c5bed157bd5ddf8f4589cfc0624296b5634590fa7a29323872987928e", operations := [] },
  { member := "ExecuteTransaction", id := "n006", kind := "statement", parentId := "n004", arm := "body", condition := "-", sha256 := "e8d1e02fe1e0c63905bf251185cb877429222a2e8cc272f56c611fc9ca7ae4b6", operations := [] },
  { member := "ExecuteTransaction", id := "n007", kind := "statement", parentId := "n004", arm := "body", condition := "-", sha256 := "39c6bdd7f027bba3c719b2c647f0eae7760fa50015f4ab5b4d6fd69f5304d021", operations := [] },
  { member := "ExecuteTransaction", id := "n008", kind := "try", parentId := "n004", arm := "body", condition := "-", sha256 := "73bdcb109c3e9c96ba4251f7559367b7bb54932a4d53e91a34389d3e433baa78", operations := ["call:ExecutePrecompile", "call:TraceTransactionActionStart", "call:AddTransferLog", "call:ExecuteCall", "call:PrepareNextCallFrame", "call:HandleException", "call:TraceTransactionActionEnd", "call:PrepareTopLevelSubstate", "call:Pop", "call:IsAnyCreate", "call:IncorporateChildStateGasRefunds", "call:Refund", "call:GetRemainingGas", "call:PrepareCreateData", "call:HandleCreate", "call:HandleRegularReturn", "call:CommitToParent", "call:IncorporateChildStateGasRefunds", "call:RepayStateGasSpill", "call:UpdateGasUp", "call:GetRemainingGas", "call:RemoveAdvancedStateGasRefund", "call:RestoreChildStateGas", "call:IsAnyCreate", "call:CreditStateGasRefund", "call:GetCreateStateCost", "call:CreditStateGasRefund", "call:GetNewAccountStateCost", "call:HandleRevert", ] },
  { member := "ExecuteTransaction", id := "n009", kind := "statement", parentId := "n008", arm := "try", condition := "-", sha256 := "e7f61ff91114fe978b89a687702faf0e40a2c65d16cf16b72fed86df87bb80d4", operations := [] },
  { member := "ExecuteTransaction", id := "n010", kind := "if", parentId := "n008", arm := "try", condition := "_currentState.IsPrecompile", sha256 := "a53c5b8066ee6cf3ab56a4ebb42723b3fbfb7e359b0b5b774c036470f671becf", operations := ["call:ExecutePrecompile", "call:TraceTransactionActionStart", "call:AddTransferLog", "call:ExecuteCall", "call:PrepareNextCallFrame", "call:HandleException", ] },
  { member := "ExecuteTransaction", id := "n011", kind := "statement", parentId := "n010", arm := "then", condition := "-", sha256 := "e9485d48c6616b1f8a850bc57e0f87a96b92ee640ad9b11765a2a72de4527a47", operations := ["call:ExecutePrecompile", ] },
  { member := "ExecuteTransaction", id := "n012", kind := "if", parentId := "n010", arm := "then", condition := "failureisnotnull", sha256 := "a3d1bc9287c7cb7783ae67e760240f8f0a4637959a5155ee5b430c9c922cf4d4", operations := [] },
  { member := "ExecuteTransaction", id := "n013", kind := "goto", parentId := "n012", arm := "then", condition := "Failure", sha256 := "6c38bf83b52780babb92a6902edecaf5eb153afe9218cefd0dbbab88956ff0be", operations := [] },
  { member := "ExecuteTransaction", id := "n014", kind := "if", parentId := "n010", arm := "else", condition := "!_currentState.IsContinuation", sha256 := "5354f1ae3dfa615a0b90071e0a987cbd8b9f31d9c43739e6fdb575e9aff71b39", operations := ["call:TraceTransactionActionStart", "call:AddTransferLog", ] },
  { member := "ExecuteTransaction", id := "n015", kind := "if", parentId := "n014", arm := "then", condition := "IsTracingActions", sha256 := "8b7abcab7533dcf8f2fd921f5acac5e2350fd794962e5fc1edbe54ff787bb03a", operations := ["call:TraceTransactionActionStart", ] },
  { member := "ExecuteTransaction", id := "n016", kind := "statement", parentId := "n015", arm := "then", condition := "-", sha256 := "edd2c3d73c3ff7f7f67101624c8065bead1800a45bc62d67907ece0b35100074", operations := ["call:TraceTransactionActionStart", ] },
  { member := "ExecuteTransaction", id := "n017", kind := "statement", parentId := "n014", arm := "then", condition := "-", sha256 := "98037efe7483d47336826b4db370129066186cacd649313388d335507b2fe48b", operations := ["call:AddTransferLog", ] },
  { member := "ExecuteTransaction", id := "n018", kind := "if", parentId := "n010", arm := "else", condition := "_currentState.Env.CodeInfoisnotnull", sha256 := "e07e70c36a5ecdc7e1d3f12383397a421c8f587164bde1d4c16c3154b011a8ff", operations := ["call:ExecuteCall", ] },
  { member := "ExecuteTransaction", id := "n019", kind := "statement", parentId := "n018", arm := "then", condition := "-", sha256 := "24210a2a8c41533165bd65354727718e8643d2f1b37e7fa05fbb6fd89159a20c", operations := ["call:ExecuteCall", ] },
  { member := "ExecuteTransaction", id := "n020", kind := "if", parentId := "n010", arm := "else", condition := "!callResult.IsReturn", sha256 := "1367fcf2e1261bc1e057d1751723da2ccb8a702313d2819a26a8180e53c077be", operations := ["call:PrepareNextCallFrame", ] },
  { member := "ExecuteTransaction", id := "n021", kind := "statement", parentId := "n020", arm := "then", condition := "-", sha256 := "0724137e1fdd1133a812219af8992bb9819e8521854de6aa133256fba67608a3", operations := ["call:PrepareNextCallFrame", ] },
  { member := "ExecuteTransaction", id := "n022", kind := "continue", parentId := "n020", arm := "then", condition := "-", sha256 := "26dde4734f8d5095653fa0b9b523c1ed38fb9c32b9c8496f8562007f474dcbdf", operations := [] },
  { member := "ExecuteTransaction", id := "n023", kind := "if", parentId := "n010", arm := "else", condition := "callResult.IsException", sha256 := "d3b44887f67b2bdfd1fa4305e0159ccb36725278a141d409e6651681842e3a30", operations := ["call:HandleException", ] },
  { member := "ExecuteTransaction", id := "n024", kind := "statement", parentId := "n023", arm := "then", condition := "-", sha256 := "3ff4c13b3d809c774ec5a6abf38ff46354fba6516f8693724edde9b6da3a963f", operations := ["call:HandleException", ] },
  { member := "ExecuteTransaction", id := "n025", kind := "if", parentId := "n023", arm := "then", condition := "terminate", sha256 := "056ff8b3e5239d47f5988515b2bc3a229a00c238f26fdd20f40b6d4c46511b52", operations := [] },
  { member := "ExecuteTransaction", id := "n026", kind := "return", parentId := "n025", arm := "then", condition := "substate", sha256 := "d637719df698ccf7ff9217327b8e470544c9371a6d68b34fca59bcd71eef2060", operations := [] },
  { member := "ExecuteTransaction", id := "n027", kind := "continue", parentId := "n023", arm := "then", condition := "-", sha256 := "26dde4734f8d5095653fa0b9b523c1ed38fb9c32b9c8496f8562007f474dcbdf", operations := [] },
  { member := "ExecuteTransaction", id := "n028", kind := "if", parentId := "n008", arm := "try", condition := "_currentState.IsTopLevel", sha256 := "38431f06bc3ed928d5b2038fbebcd9d74540ee6ea4d2fac00a8cb1433f9de77e", operations := ["call:TraceTransactionActionEnd", "call:PrepareTopLevelSubstate", ] },
  { member := "ExecuteTransaction", id := "n029", kind := "if", parentId := "n028", arm := "then", condition := "IsTracingActions", sha256 := "e567159f9d679041e5e8533a99f88729c53601894fe600cf6eda7eab2d160e75", operations := ["call:TraceTransactionActionEnd", ] },
  { member := "ExecuteTransaction", id := "n030", kind := "statement", parentId := "n029", arm := "then", condition := "-", sha256 := "d47056d3d64259d5e52e5e32d4bfbda21a27995f4535dc26b3910a1493a60dc3", operations := ["call:TraceTransactionActionEnd", ] },
  { member := "ExecuteTransaction", id := "n031", kind := "statement", parentId := "n028", arm := "then", condition := "-", sha256 := "a6cac4e1154e2cbf608d748f078acda0acbee18f635303bbe87071166553722a", operations := ["call:PrepareTopLevelSubstate", ] },
  { member := "ExecuteTransaction", id := "n032", kind := "return", parentId := "n028", arm := "then", condition := "substate", sha256 := "d637719df698ccf7ff9217327b8e470544c9371a6d68b34fca59bcd71eef2060", operations := [] },
  { member := "ExecuteTransaction", id := "n033", kind := "using", parentId := "n008", arm := "try", condition := "VmState<TGasPolicy>previousState=_currentState", sha256 := "69926b49fae5c27e3c3e45e873ab98edd0895a1bea9bf8d13c0f5c61a45cdf98", operations := ["call:Pop", "call:IsAnyCreate", "call:IncorporateChildStateGasRefunds", "call:Refund", "call:GetRemainingGas", "call:PrepareCreateData", "call:HandleCreate", "call:HandleRegularReturn", "call:CommitToParent", "call:IncorporateChildStateGasRefunds", "call:RepayStateGasSpill", "call:UpdateGasUp", "call:GetRemainingGas", "call:RemoveAdvancedStateGasRefund", "call:RestoreChildStateGas", "call:IsAnyCreate", "call:CreditStateGasRefund", "call:GetCreateStateCost", "call:CreditStateGasRefund", "call:GetNewAccountStateCost", "call:HandleRevert", ] },
  { member := "ExecuteTransaction", id := "n034", kind := "statement", parentId := "n033", arm := "body", condition := "-", sha256 := "02c1204a0a8f95fa12a8967dd4db8a3c457fd15011f43ac336f436fc6b7b4f98", operations := ["call:Pop", ] },
  { member := "ExecuteTransaction", id := "n035", kind := "statement", parentId := "n033", arm := "body", condition := "-", sha256 := "ea842315341f5ac822ae5e5deec682e875947bb2f53f80943fd3a407a901bbca", operations := [] },
  { member := "ExecuteTransaction", id := "n036", kind := "if", parentId := "n033", arm := "body", condition := "!callResult.ShouldRevert", sha256 := "8c51e72ae9c304a88bc347a2888076cffaf83ba53ee133515c2453e92fdf9db1", operations := ["call:IsAnyCreate", "call:IncorporateChildStateGasRefunds", "call:Refund", "call:GetRemainingGas", "call:PrepareCreateData", "call:HandleCreate", "call:HandleRegularReturn", "call:CommitToParent", "call:IncorporateChildStateGasRefunds", "call:RepayStateGasSpill", "call:UpdateGasUp", "call:GetRemainingGas", "call:RemoveAdvancedStateGasRefund", "call:RestoreChildStateGas", "call:IsAnyCreate", "call:CreditStateGasRefund", "call:GetCreateStateCost", "call:CreditStateGasRefund", "call:GetNewAccountStateCost", "call:HandleRevert", ] },
  { member := "ExecuteTransaction", id := "n037", kind := "statement", parentId := "n036", arm := "then", condition := "-", sha256 := "78e038f7c8e0ea6bc785086b0b7829605d085e910e2a69d3fde0e0809ca6a9eb", operations := ["call:IsAnyCreate", ] },
  { member := "ExecuteTransaction", id := "n038", kind := "if", parentId := "n036", arm := "then", condition := "!isCreate", sha256 := "dcd57a62fc3146f8db4766cb4972a199e7f7edbd05e31f87321f022137927424", operations := ["call:IncorporateChildStateGasRefunds", ] },
  { member := "ExecuteTransaction", id := "n039", kind := "statement", parentId := "n038", arm := "then", condition := "-", sha256 := "d7ab358e74087a3859024df64ac21155df0bc7021fd3c2a083b155a49293b4a6", operations := ["call:IncorporateChildStateGasRefunds", ] },
  { member := "ExecuteTransaction", id := "n040", kind := "statement", parentId := "n036", arm := "then", condition := "-", sha256 := "dd813b9a459e4ea0e757e2db4778ae5cbbd51a5c53c0ce133493287649db4f20", operations := ["call:Refund", ] },
  { member := "ExecuteTransaction", id := "n041", kind := "if", parentId := "n036", arm := "then", condition := "isCreate", sha256 := "aca06b3e6fdeba0bae15d3238d297b1b093036482d826abc51b4428b3d4799e2", operations := ["call:GetRemainingGas", "call:PrepareCreateData", "call:HandleCreate", "call:HandleRegularReturn", ] },
  { member := "ExecuteTransaction", id := "n042", kind := "statement", parentId := "n041", arm := "then", condition := "-", sha256 := "ca9658362ef9266631c0945012d9fc9f265abbc5ea89b5c9c51801a560c8290c", operations := ["call:GetRemainingGas", ] },
  { member := "ExecuteTransaction", id := "n043", kind := "statement", parentId := "n041", arm := "then", condition := "-", sha256 := "b38c746e559b46de9426d33dd14e3fc5a5dfbff21745380b5048569754f5b11c", operations := ["call:PrepareCreateData", ] },
  { member := "ExecuteTransaction", id := "n044", kind := "statement", parentId := "n041", arm := "then", condition := "-", sha256 := "e172f1ea0637d372d2b6122e048ba1fb79834a46a0ca03ab172ceb739ea1097f", operations := ["call:HandleCreate", ] },
  { member := "ExecuteTransaction", id := "n045", kind := "statement", parentId := "n041", arm := "else", condition := "-", sha256 := "ad28cd96d64b9aeebc155ae6e64da48e07a3521838acc0644cb064c232ba639b", operations := ["call:HandleRegularReturn", ] },
  { member := "ExecuteTransaction", id := "n046", kind := "if", parentId := "n036", arm := "then", condition := "previousStateSucceeded", sha256 := "6df68e203f885c60ae0c21f3042976e3c9221ff2157598badce68dbab0d7619e", operations := ["call:CommitToParent", "call:IncorporateChildStateGasRefunds", "call:RepayStateGasSpill", ] },
  { member := "ExecuteTransaction", id := "n047", kind := "statement", parentId := "n046", arm := "then", condition := "-", sha256 := "0927e1d24549dc0ac267486606958a5769b497f65c6ca6d7f9411a18de5e20b7", operations := ["call:CommitToParent", ] },
  { member := "ExecuteTransaction", id := "n048", kind := "if", parentId := "n046", arm := "then", condition := "isCreate", sha256 := "12fee639700ab2951c942deef372cddedbf276ce9851920c1758b514c2738439", operations := ["call:IncorporateChildStateGasRefunds", ] },
  { member := "ExecuteTransaction", id := "n049", kind := "statement", parentId := "n048", arm := "then", condition := "-", sha256 := "d7ab358e74087a3859024df64ac21155df0bc7021fd3c2a083b155a49293b4a6", operations := ["call:IncorporateChildStateGasRefunds", ] },
  { member := "ExecuteTransaction", id := "n050", kind := "statement", parentId := "n046", arm := "then", condition := "-", sha256 := "c8477e0046464c1cb136a95db616ac7d1e9a5c8e52bf48b780171c5a39b5880f", operations := ["call:RepayStateGasSpill", ] },
  { member := "ExecuteTransaction", id := "n051", kind := "statement", parentId := "n036", arm := "else", condition := "-", sha256 := "533969bc9247300f226350b9e1af1cfa15c004cefae4db150b760b2139ee2acf", operations := ["call:UpdateGasUp", "call:GetRemainingGas", ] },
  { member := "ExecuteTransaction", id := "n052", kind := "statement", parentId := "n036", arm := "else", condition := "-", sha256 := "24f414a1e837b84d43ae8aece77f496320992f8289f50ddf38c9365fadf3b420", operations := ["call:RemoveAdvancedStateGasRefund", ] },
  { member := "ExecuteTransaction", id := "n053", kind := "statement", parentId := "n036", arm := "else", condition := "-", sha256 := "a216bf731315e4b17290dbac4d66143d7489df7d8f201f82c7e6f02b46c32508", operations := ["call:RestoreChildStateGas", ] },
  { member := "ExecuteTransaction", id := "n054", kind := "if", parentId := "n036", arm := "else", condition := "previousState.ExecutionType.IsAnyCreate()&&previousState.IsCreateStateGasCharged", sha256 := "aa2509e023259363b1a110536ae29cfa5f74d5263a8130c6db96e5c29fa1e999", operations := ["call:IsAnyCreate", "call:CreditStateGasRefund", "call:GetCreateStateCost", "call:CreditStateGasRefund", "call:GetNewAccountStateCost", ] },
  { member := "ExecuteTransaction", id := "n055", kind := "statement", parentId := "n054", arm := "then", condition := "-", sha256 := "c807889fb784f2c334e4e1d8f686be2f88d303a6137655a2326eae21fe0c85fa", operations := ["call:CreditStateGasRefund", "call:GetCreateStateCost", ] },
  { member := "ExecuteTransaction", id := "n056", kind := "if", parentId := "n054", arm := "else", condition := "previousState.NewAccountCharged", sha256 := "64ca300989968e5b7b7ebe0273f97bca60a29b9e1574be08b2bc91821dccc419", operations := ["call:CreditStateGasRefund", "call:GetNewAccountStateCost", ] },
  { member := "ExecuteTransaction", id := "n057", kind := "statement", parentId := "n056", arm := "then", condition := "-", sha256 := "cd5eceb13f7ac05276b330421b4081a70fade82e4d3f4173efa21833cd5e2dd5", operations := ["call:CreditStateGasRefund", "call:GetNewAccountStateCost", ] },
  { member := "ExecuteTransaction", id := "n058", kind := "statement", parentId := "n036", arm := "else", condition := "-", sha256 := "a499b5b59a2e70a9ff1a2365af2faf00aca07ce361f1743ee479b74687903596", operations := ["call:HandleRevert", ] },
  { member := "ExecuteTransaction", id := "n059", kind := "catch", parentId := "n008", arm := "catch", condition := "(Exceptionex)exisEvmExceptionorOverflowException", sha256 := "893fe4c7faf56bb1cbc3f8873122c073d000e5e315fb9d5278cd8acffd2368b7", operations := [] },
  { member := "ExecuteTransaction", id := "n060", kind := "goto", parentId := "n059", arm := "catch", condition := "Failure", sha256 := "6c38bf83b52780babb92a6902edecaf5eb153afe9218cefd0dbbab88956ff0be", operations := [] },
  { member := "ExecuteTransaction", id := "n061", kind := "continue", parentId := "n004", arm := "body", condition := "-", sha256 := "26dde4734f8d5095653fa0b9b523c1ed38fb9c32b9c8496f8562007f474dcbdf", operations := [] },
  { member := "ExecuteTransaction", id := "n062", kind := "label", parentId := "n004", arm := "body", condition := "Failure", sha256 := "ffa86643191910814e258f7f8145c4f2e66677108ab7ed5a2c7639b5d6aaec08", operations := ["call:HandleFailure", ] },
  { member := "ExecuteTransaction", id := "n063", kind := "statement", parentId := "n062", arm := "body", condition := "-", sha256 := "8ea948a86bdd8570f2a412a14c727d727cc76d9cbf7781044b7b8b36c60b5ea2", operations := ["call:HandleFailure", ] },
  { member := "ExecuteTransaction", id := "n064", kind := "if", parentId := "n004", arm := "body", condition := "shouldExit", sha256 := "393e2852ac95ca65609c146c611cdd29d02328a37a48b25af455f69f7ad42734", operations := [] },
  { member := "ExecuteTransaction", id := "n065", kind := "return", parentId := "n064", arm := "then", condition := "failSubstate", sha256 := "fb142761c0defa38a41b56db03a96106e7e1f9cc2b58a17881aacef3ad2273c3", operations := [] },
  { member := "RunByteCode", id := "n000", kind := "statement", parentId := "root", arm := "body", condition := "-", sha256 := "704dac8d08376c7fa8452c8f41b0f736de5cee18b415bde0400ee16158e01458", operations := [] },
  { member := "RunByteCode", id := "n001", kind := "statement", parentId := "root", arm := "body", condition := "-", sha256 := "69bc7fe7b58da10d67a4e5ccbbed2ae11af19dffdde162e2b5b4c1ba91128a80", operations := ["call:RunDispatchLoop", ] },
  { member := "RunByteCode", id := "n002", kind := "if", parentId := "root", arm := "body", condition := "exceptionTypeisEvmExceptionType.NoneorEvmExceptionType.StoporEvmExceptionType.RevertorEvmExceptionType.Suspend", sha256 := "79fa002adea49c691cf613fd0f0ae1d962a3c72c462431b78db258a5ee82e504", operations := ["call:IsAnyCreate", "call:EndInstructionTrace", "call:GetRemainingGas", ] },
  { member := "RunByteCode", id := "n003", kind := "if", parentId := "n002", arm := "then", condition := "TTracingInst.IsActive&&exceptionType!=EvmExceptionType.None&&(exceptionType!=EvmExceptionType.Suspend||ReturnDataisnotVmState<TGasPolicy>childState||!childState.ExecutionType.IsAnyCreate())", sha256 := "90e81052ea568734e7753850c166352e01f306452b48249e780883fb90cfefb5", operations := ["call:IsAnyCreate", "call:EndInstructionTrace", "call:GetRemainingGas", ] },
  { member := "RunByteCode", id := "n004", kind := "statement", parentId := "n003", arm := "then", condition := "-", sha256 := "d4ec1b817504c52dec395ec5f748a37cb27f1a6d223c98a654568c18538579f2", operations := ["call:EndInstructionTrace", "call:GetRemainingGas", ] },
  { member := "RunByteCode", id := "n005", kind := "statement", parentId := "n002", arm := "then", condition := "-", sha256 := "863355e47116c79f000e56d7eea088fba7035748ada17cb72514dfb9b28c3934", operations := [] },
  { member := "RunByteCode", id := "n006", kind := "statement", parentId := "n002", arm := "then", condition := "-", sha256 := "5cf365e0e1231a34fdfd8d5d643adc77a26c29a03cb4bf37e97fbd5b64408b4d", operations := [] },
  { member := "RunByteCode", id := "n007", kind := "goto", parentId := "n002", arm := "else", condition := "ReturnFailure", sha256 := "a0ad3886457376c964d2a177753d9a4dca8bcb29d97e4b663ec01fcb08338050", operations := [] },
  { member := "RunByteCode", id := "n008", kind := "statement", parentId := "root", arm := "body", condition := "-", sha256 := "b2a7ce5196be8e9db8daf41061e1b36790911729d2bc3b29494b9d7b7456352b", operations := ["call:Assert", ] },
  { member := "RunByteCode", id := "n009", kind := "if", parentId := "root", arm := "body", condition := "exceptionType==EvmExceptionType.Suspend", sha256 := "ba792e3f856d56d982e7c6be46c5bc1aecdb300a1bde9a5b0d288f1c904320a4", operations := ["call:As", ] },
  { member := "RunByteCode", id := "n010", kind := "return", parentId := "n009", arm := "then", condition := "newCallResult(Unsafe.As<VmState<TGasPolicy>>(ReturnData!))", sha256 := "db5dbf9745b79dff6f92bdbb2b73434156abc4e605cf4dd23961d650b236dbf2", operations := ["call:As", ] },
  { member := "RunByteCode", id := "n011", kind := "if", parentId := "root", arm := "body", condition := "exceptionType==EvmExceptionType.Revert", sha256 := "9b93723c0395d8b6de0ca32dbba55109af86c01caf7c452c134ef923a8f64bac", operations := [] },
  { member := "RunByteCode", id := "n012", kind := "goto", parentId := "n011", arm := "then", condition := "Revert", sha256 := "8395a50a6e46d5684994f309bd2ff345eb4ff430bdfa4cdba154eb4af4a79f72", operations := [] },
  { member := "RunByteCode", id := "n013", kind := "if", parentId := "root", arm := "body", condition := "ReturnDataisnotnull", sha256 := "e0913d4906fcf8649a3b229a77128af4ca3ac435f083013a9586998a9f7ff081", operations := [] },
  { member := "RunByteCode", id := "n014", kind := "goto", parentId := "n013", arm := "then", condition := "DataReturn", sha256 := "66ea7762c456be458ccb759b14f3281f3cf209a081d60620c511ebb015ef2d37", operations := [] },
  { member := "RunByteCode", id := "n015", kind := "return", parentId := "root", arm := "body", condition := "CallResult.Empty()", sha256 := "261c1113a4bf31d89acae7948ce0c0dbe28d8605aa73afde90439f1bbf2a3505", operations := ["call:Empty", ] },
  { member := "RunByteCode", id := "n016", kind := "label", parentId := "root", arm := "body", condition := "DataReturn", sha256 := "2ab61853cd4c30e21213ef2891ba03d2b291c5677f78dc00f1b580fed39347bc", operations := ["call:Assert", ] },
  { member := "RunByteCode", id := "n017", kind := "statement", parentId := "n016", arm := "body", condition := "-", sha256 := "af2ebd87a3e6f2634a6f6f2596cfb01441fab9a8a30fa38fc58924e5a14a60f2", operations := ["call:Assert", ] },
  { member := "RunByteCode", id := "n018", kind := "return", parentId := "root", arm := "body", condition := "newCallResult(Unsafe.As<byte[]>(ReturnData),null)", sha256 := "382772a17f572b9102a61262ddbd6416e0a24a96dfa18b57ed1f403948763c69", operations := ["call:As", ] },
  { member := "RunByteCode", id := "n019", kind := "label", parentId := "root", arm := "body", condition := "Revert", sha256 := "f5ed42e6ee860302754c43ea7e97a784982870e02bffed26f0f91f06a23f440c", operations := ["call:Assert", ] },
  { member := "RunByteCode", id := "n020", kind := "statement", parentId := "n019", arm := "body", condition := "-", sha256 := "86f53728432a33783cb5b54c438626afada740e23554e27aa785c345e31f4819", operations := ["call:Assert", ] },
  { member := "RunByteCode", id := "n021", kind := "return", parentId := "root", arm := "body", condition := "newCallResult(Unsafe.As<byte[]>(ReturnData),null,shouldRevert:true,exceptionType)", sha256 := "e27e2b20bf6ee06caf5e4a1ca965f89847f4823e8369367f9f5cb01478143a09", operations := ["call:As", ] },
  { member := "RunByteCode", id := "n022", kind := "label", parentId := "root", arm := "body", condition := "ReturnFailure", sha256 := "629346c4e429a4427a80a231170e4eed63f1e9b5bd0e7b1919b8a37e85d292f0", operations := ["call:ClearExecutionGas", ] },
  { member := "RunByteCode", id := "n023", kind := "if", parentId := "n022", arm := "body", condition := "exceptionType==EvmExceptionType.OutOfGas", sha256 := "175a014248fd04b6f9a729421bbf94ea4c21e3106c8ae1f5be9f212f288291bc", operations := ["call:ClearExecutionGas", ] },
  { member := "RunByteCode", id := "n024", kind := "statement", parentId := "n023", arm := "then", condition := "-", sha256 := "fde12da0e92eadb64e1354e6438420176257067ca1f2cd50103a7de90b19addc", operations := ["call:ClearExecutionGas", ] },
  { member := "RunByteCode", id := "n025", kind := "return", parentId := "root", arm := "body", condition := "GetFailureReturn(TGasPolicy.GetRemainingGas(ingas),exceptionType)", sha256 := "53cadf22e7f909c6e9405fbb122e4f6f372fd0d38e64bbc527c6b4544e01bebc", operations := ["call:GetFailureReturn", "call:GetRemainingGas", ] },
  { member := "RunDispatchLoop", id := "n000", kind := "if", parentId := "root", arm := "body", condition := "(nuint)programCounter>=(nuint)stack.CodeLength", sha256 := "e0de3de7af97a8e75a230857e306e8030f5b9ad94229ca7fd51dd2ca943ad67f", operations := [] },
  { member := "RunDispatchLoop", id := "n001", kind := "return", parentId := "n000", arm := "then", condition := "EvmExceptionType.None", sha256 := "73187d8757adf3389858b3478774762459dabc79406daf66169415f810306d4c", operations := [] },
  { member := "RunDispatchLoop", id := "n002", kind := "statement", parentId := "root", arm := "body", condition := "-", sha256 := "c81cd1b6f71070c439945b098acd1aecf42962c9cb314c789943bbdf97fab038", operations := [] },
  { member := "RunDispatchLoop", id := "n003", kind := "if", parentId := "root", arm := "body", condition := "!TCancelable.IsActive", sha256 := "48af0348e77989abe6a72a037fa2b9d3fe44e24837a5d55a88756e4b5553929d", operations := ["call:Add", ] },
  { member := "RunDispatchLoop", id := "n004", kind := "statement", parentId := "n003", arm := "then", condition := "-", sha256 := "48a251643c6cc23268aa8cccbec8f6586f77a9eeb0e7fdff7d6a4056c4329494", operations := [] },
  { member := "RunDispatchLoop", id := "n005", kind := "statement", parentId := "n003", arm := "then", condition := "-", sha256 := "d8c6e91053325dc16d16047e094543ccb5c77af285800a6df0f9c16f7c789421", operations := ["call:Add", ] },
  { member := "RunDispatchLoop", id := "n006", kind := "statement", parentId := "n003", arm := "then", condition := "-", sha256 := "b64550ecd01650a9b486ca361664590e975ca1bb911efacfee080766aba5154a", operations := [] },
  { member := "RunDispatchLoop", id := "n007", kind := "return", parentId := "n003", arm := "then", condition := "ordinaryExceptionType", sha256 := "0621b33cecff616f62c17736355ad93e55017503a4985875885f5bd7ae1362ed", operations := [] },
  { member := "RunDispatchLoop", id := "n008", kind := "statement", parentId := "root", arm := "body", condition := "-", sha256 := "b09dbac268ea40223b070a11886dfdc50853b74c0fa0c2f5c369bb1a81b6200b", operations := [] },
  { member := "RunDispatchLoop", id := "n009", kind := "if", parentId := "root", arm := "body", condition := "_txTracer.IsCancelled", sha256 := "4acbbc39ab912e75c207459ba2f54a58332a52b164a2f4a9e05bc6425673f2df", operations := ["call:ThrowOperationCanceledException", ] },
  { member := "RunDispatchLoop", id := "n010", kind := "statement", parentId := "n009", arm := "then", condition := "-", sha256 := "4d324191e89b183a0406874e36e5069c41df83e7628f96a5418f4ee66b44317a", operations := ["call:ThrowOperationCanceledException", ] },
  { member := "RunDispatchLoop", id := "n011", kind := "statement", parentId := "root", arm := "body", condition := "-", sha256 := "cec935c647289b49e803df9efd77d156bdff581263331ec2a2962e7230221d05", operations := [] },
  { member := "RunDispatchLoop", id := "n012", kind := "statement", parentId := "root", arm := "body", condition := "-", sha256 := "effbf7cc9434a3e9bc11c7a3d85acd02c384bce5d687b1534425e10baeb83714", operations := [] },
  { member := "RunDispatchLoop", id := "n013", kind := "statement", parentId := "root", arm := "body", condition := "-", sha256 := "0540152dcec67ad830a53b20bad562bb8264ec71f25d4b985b809f30cbc94c3c", operations := [] },
  { member := "RunDispatchLoop", id := "n014", kind := "while", parentId := "root", arm := "body", condition := "true", sha256 := "d747c88f027e4a7d6124d69fe3c9326461d24aae2c1620dd508d2de0999c603d", operations := ["call:Add", "call:ThrowOperationCanceledException", ] },
  { member := "RunDispatchLoop", id := "n015", kind := "statement", parentId := "n014", arm := "body", condition := "-", sha256 := "c396ec1e11b35138a2d43557a7da8a87d2d7a2afd06e8db4769265704c67cc1b", operations := ["call:Add", ] },
  { member := "RunDispatchLoop", id := "n016", kind := "statement", parentId := "n014", arm := "body", condition := "-", sha256 := "8c4714b04a93f6ed0a1247e166314f8766f480a3569d76c34e48cba05ed9b473", operations := [] },
  { member := "RunDispatchLoop", id := "n017", kind := "if", parentId := "n014", arm := "body", condition := "exceptionType!=EvmExceptionType.None||(cancelableState.OpCodeCount&CancellationCheckMask)!=0||(nuint)cancelableState.FinalProgramCounter>=(nuint)stack.CodeLength", sha256 := "f417e271fa9221ecb3c3ad615c0f255eb9e4d80842a10ded0d8fee2ec58d38e3", operations := [] },
  { member := "RunDispatchLoop", id := "n018", kind := "break", parentId := "n017", arm := "then", condition := "-", sha256 := "5c6865654bf74e1c74b29edeff03cdf0a7ab7bd9d4ab263e9247ca1269e98d27", operations := [] },
  { member := "RunDispatchLoop", id := "n019", kind := "if", parentId := "n014", arm := "body", condition := "_txTracer.IsCancelled", sha256 := "4acbbc39ab912e75c207459ba2f54a58332a52b164a2f4a9e05bc6425673f2df", operations := ["call:ThrowOperationCanceledException", ] },
  { member := "RunDispatchLoop", id := "n020", kind := "statement", parentId := "n019", arm := "then", condition := "-", sha256 := "4d324191e89b183a0406874e36e5069c41df83e7628f96a5418f4ee66b44317a", operations := ["call:ThrowOperationCanceledException", ] },
  { member := "RunDispatchLoop", id := "n021", kind := "return", parentId := "root", arm := "body", condition := "exceptionType", sha256 := "67bf00da55a79bd487f082d6aaff26b7a1ac84945aa53478ebd03e1540ddaf42", operations := [] },
  { member := "Dispose", id := "n000", kind := "if", parentId := "root", arm := "body", condition := "vm._currentStateisnotnull||vm._stateStack.Count!=0", sha256 := "4600694c3317fa922e41a4446287e109f0b07cb66b23178f7180fbe83e82a704", operations := ["call:DisposeActiveFrames", ] },
  { member := "Dispose", id := "n001", kind := "statement", parentId := "n000", arm := "then", condition := "-", sha256 := "87c615227e527913f71472eceafc45a6d4fcd3c81b6856aa280803900cc6b7d6", operations := ["call:DisposeActiveFrames", ] },
]

def admittedBranchBindings : List BranchBinding := [
  { branch := "FreshPreparation", member := "ExecuteTransaction", nodeId := "n005", kind := "if", sourceArm := "body", sourceSha256 := "dad25b4c5bed157bd5ddf8f4589cfc0624296b5634590fa7a29323872987928e", arm := "then" },
  { branch := "ContinuationPreparation", member := "ExecuteTransaction", nodeId := "n005", kind := "if", sourceArm := "body", sourceSha256 := "dad25b4c5bed157bd5ddf8f4589cfc0624296b5634590fa7a29323872987928e", arm := "else" },
  { branch := "BytecodeDispatch", member := "ExecuteTransaction", nodeId := "n010", kind := "if", sourceArm := "try", sourceSha256 := "a53c5b8066ee6cf3ab56a4ebb42723b3fbfb7e359b0b5b774c036470f671becf", arm := "else" },
  { branch := "BytecodeDispatch", member := "RunByteCode", nodeId := "n001", kind := "statement", sourceArm := "body", sourceSha256 := "69bc7fe7b58da10d67a4e5ccbbed2ae11af19dffdde162e2b5b4c1ba91128a80", arm := "node" },
  { branch := "FullPrecompileDispatch", member := "ExecuteTransaction", nodeId := "n010", kind := "if", sourceArm := "try", sourceSha256 := "a53c5b8066ee6cf3ab56a4ebb42723b3fbfb7e359b0b5b774c036470f671becf", arm := "then" },
  { branch := "FullPrecompileDispatch", member := "ExecuteTransaction", nodeId := "n011", kind := "statement", sourceArm := "then", sourceSha256 := "e9485d48c6616b1f8a850bc57e0f87a96b92ee640ad9b11765a2a72de4527a47", arm := "node" },
  { branch := "BytecodeContinue", member := "ExecuteTransaction", nodeId := "n020", kind := "if", sourceArm := "else", sourceSha256 := "1367fcf2e1261bc1e057d1751723da2ccb8a702313d2819a26a8180e53c077be", arm := "continue" },
  { branch := "BytecodeSuspend", member := "ExecuteTransaction", nodeId := "n021", kind := "statement", sourceArm := "then", sourceSha256 := "0724137e1fdd1133a812219af8992bb9819e8521854de6aa133256fba67608a3", arm := "node" },
  { branch := "NestedRegularSuccess", member := "ExecuteTransaction", nodeId := "n038", kind := "if", sourceArm := "then", sourceSha256 := "dcd57a62fc3146f8db4766cb4972a199e7f7edbd05e31f87321f022137927424", arm := "regular" },
  { branch := "NestedCreateSuccess", member := "ExecuteTransaction", nodeId := "n041", kind := "if", sourceArm := "then", sourceSha256 := "aca06b3e6fdeba0bae15d3238d297b1b093036482d826abc51b4428b3d4799e2", arm := "node" },
  { branch := "NestedCreateInvalidCode", member := "ExecuteTransaction", nodeId := "n044", kind := "statement", sourceArm := "then", sourceSha256 := "e172f1ea0637d372d2b6122e048ba1fb79834a46a0ca03ab172ceb739ea1097f", arm := "invalidCode" },
  { branch := "NestedCreateOutOfGas", member := "ExecuteTransaction", nodeId := "n044", kind := "statement", sourceArm := "then", sourceSha256 := "e172f1ea0637d372d2b6122e048ba1fb79834a46a0ca03ab172ceb739ea1097f", arm := "outOfGas" },
  { branch := "NestedRevert", member := "ExecuteTransaction", nodeId := "n058", kind := "statement", sourceArm := "else", sourceSha256 := "a499b5b59a2e70a9ff1a2365af2faf00aca07ce361f1743ee479b74687903596", arm := "node" },
  { branch := "NestedException", member := "ExecuteTransaction", nodeId := "n024", kind := "statement", sourceArm := "then", sourceSha256 := "3ff4c13b3d809c774ec5a6abf38ff46354fba6516f8693724edde9b6da3a963f", arm := "node" },
  { branch := "TopLevelSuccess", member := "ExecuteTransaction", nodeId := "n031", kind := "statement", sourceArm := "then", sourceSha256 := "a6cac4e1154e2cbf608d748f078acda0acbee18f635303bbe87071166553722a", arm := "node" },
  { branch := "TopLevelRevert", member := "ExecuteTransaction", nodeId := "n031", kind := "statement", sourceArm := "then", sourceSha256 := "a6cac4e1154e2cbf608d748f078acda0acbee18f635303bbe87071166553722a", arm := "revert" },
  { branch := "TopLevelException", member := "ExecuteTransaction", nodeId := "n063", kind := "statement", sourceArm := "body", sourceSha256 := "8ea948a86bdd8570f2a412a14c727d727cc76d9cbf7781044b7b8b36c60b5ea2", arm := "node" },
  { branch := "FullPrecompileOutOfGasNested", member := "ExecuteTransaction", nodeId := "n011", kind := "statement", sourceArm := "then", sourceSha256 := "e9485d48c6616b1f8a850bc57e0f87a96b92ee640ad9b11765a2a72de4527a47", arm := "outOfGas" },
  { branch := "FullPrecompileOutOfGasTop", member := "ExecuteTransaction", nodeId := "n011", kind := "statement", sourceArm := "then", sourceSha256 := "e9485d48c6616b1f8a850bc57e0f87a96b92ee640ad9b11765a2a72de4527a47", arm := "outOfGas" },
  { branch := "FullPrecompileReturnedFailure", member := "ExecuteTransaction", nodeId := "n011", kind := "statement", sourceArm := "then", sourceSha256 := "e9485d48c6616b1f8a850bc57e0f87a96b92ee640ad9b11765a2a72de4527a47", arm := "returnedFailure" },
  { branch := "FullPrecompileManagedException", member := "ExecuteTransaction", nodeId := "n011", kind := "statement", sourceArm := "then", sourceSha256 := "e9485d48c6616b1f8a850bc57e0f87a96b92ee640ad9b11765a2a72de4527a47", arm := "managedException" },
  { branch := "Cancelled", member := "RunDispatchLoop", nodeId := "n010", kind := "statement", sourceArm := "then", sourceSha256 := "4d324191e89b183a0406874e36e5069c41df83e7628f96a5418f4ee66b44317a", arm := "cancelled" },
  { branch := "Cancelled", member := "RunDispatchLoop", nodeId := "n020", kind := "statement", sourceArm := "then", sourceSha256 := "4d324191e89b183a0406874e36e5069c41df83e7628f96a5418f4ee66b44317a", arm := "cancelled" },
  { branch := "Escaped", member := "ExecuteTransaction", nodeId := "n008", kind := "try", sourceArm := "body", sourceSha256 := "73bdcb109c3e9c96ba4251f7559367b7bb54932a4d53e91a34389d3e433baa78", arm := "node" },
  { branch := "Escaped", member := "Dispose", nodeId := "n001", kind := "statement", sourceArm := "then", sourceSha256 := "87c615227e527913f71472eceafc45a6d4fcd3c81b6856aa280803900cc6b7d6", arm := "node" },
  { branch := "InvalidControl", member := "RunDispatchLoop", nodeId := "n014", kind := "while", sourceArm := "body", sourceSha256 := "d747c88f027e4a7d6124d69fe3c9326461d24aae2c1620dd508d2de0999c603d", arm := "failClosed" },
]

def admittedControlPlan : List ControlPlanStep := [
  { stage := ControlPlanStage.prepare, member := "ExecuteTransaction", nodeId := "n005", kind := "if", sourceArm := "body", sourceSha256 := "dad25b4c5bed157bd5ddf8f4589cfc0624296b5634590fa7a29323872987928e" },
  { stage := ControlPlanStage.dispatch, member := "ExecuteTransaction", nodeId := "n010", kind := "if", sourceArm := "try", sourceSha256 := "a53c5b8066ee6cf3ab56a4ebb42723b3fbfb7e359b0b5b774c036470f671becf" },
  { stage := ControlPlanStage.classify, member := "ExecuteTransaction", nodeId := "n020", kind := "if", sourceArm := "else", sourceSha256 := "1367fcf2e1261bc1e057d1751723da2ccb8a702313d2819a26a8180e53c077be" },
  { stage := ControlPlanStage.settle, member := "ExecuteTransaction", nodeId := "n045", kind := "statement", sourceArm := "else", sourceSha256 := "ad28cd96d64b9aeebc155ae6e64da48e07a3521838acc0644cb064c232ba639b" },
  { stage := ControlPlanStage.cleanup, member := "Dispose", nodeId := "n001", kind := "statement", sourceArm := "then", sourceSha256 := "87c615227e527913f71472eceafc45a6d4fcd3c81b6856aa280803900cc6b7d6" },
]

def controlTopologyReady : Bool :=
  admittedControlNodes.length > 0 &&
  admittedBranchBindings.length > 0 &&
  admittedBranchNames.length == admittedBranchContracts.length &&
  admittedBranchNames.all (fun branch =>
    admittedBranchBindings.any (fun binding => binding.branch == branch)) &&
  admittedBranchBindings.all (fun binding =>
    admittedControlNodes.any (fun node =>
      node.member == binding.member && node.id == binding.nodeId && node.kind == binding.kind &&
      node.arm == binding.sourceArm && node.sha256 == binding.sourceSha256))

def controlPlanNodeReady (step : ControlPlanStep) : Bool :=
  admittedControlNodes.any (fun node =>
    node.member == step.member && node.id == step.nodeId && node.kind == step.kind &&
    node.arm == step.sourceArm && node.sha256 == step.sourceSha256)

def controlPlanStagesReady : Bool :=
  match admittedControlPlan with
  | { stage := ControlPlanStage.prepare, .. } :: { stage := ControlPlanStage.dispatch, .. } ::
      { stage := ControlPlanStage.classify, .. } :: { stage := ControlPlanStage.settle, .. } ::
      { stage := ControlPlanStage.cleanup, .. } :: [] => true
  | _ => false

def controlPlanReady : Bool :=
  controlPlanStagesReady && admittedControlPlan.all controlPlanNodeReady

def sourceControlReady : Bool :=
  Eip803x.Evm.FrameDriver.sourceControlReady admittedSourceControl

def prepare (leaves : DriverLeaves) (machine : Machine) : Machine :=
  match machine.current.phase with
  | .fresh => leaves.preparation.prepareFresh (leaves.preparation.clearReturnData machine)
  | .continuation => leaves.preparation.prepareContinuation machine
  | .running => machine

def dispatch (leaves : DriverLeaves) (machine : Machine) : Invocation :=
  match subjectOf machine with
  | .bytecode =>
      let outcome := leaves.dispatch.runBytecode machine
      .bytecode outcome.outcome outcome.directInlineStaticPrecompile
  | .fullPrecompile =>
      .fullPrecompile (leaves.dispatch.runFullPrecompile machine)

def classifyReturnedHalt (machine : Machine) (result : FrameResult) : SettlementRoute :=
  returnedRoute machine result

def classifyBytecodeStep (machine : Machine) (step : MachineStep) : SettlementRoute :=
  match step.result with
  | .continue _ => .continued
  | .suspend _ _ => .suspended
  | .halt result => classifyReturnedHalt machine result

def classifyPrecompileStep (machine : Machine) (step : MachineStep) : SettlementRoute :=
  match step.result with
  | .continue _ | .suspend _ _ => .invalidControl
  | .halt result =>
      match result.precompileSuccess with
      | some false =>
          if topLevel machine then .fullPrecompileReturnedFailureTop
          else .fullPrecompileReturnedFailureNested
      | _ => classifyReturnedHalt machine result

def classifyInvocation (machine : Machine) : Invocation → SettlementRoute
  | .bytecode (.returned step) _ => classifyBytecodeStep machine step
  | .bytecode (.thrownEvm _) _ =>
      if topLevel machine then .topLevelException else .childException
  | .bytecode .thrownOverflow _ =>
      if topLevel machine then .topLevelException else .childException
  | .bytecode (.escaped _) _ => .escaped
  | .bytecode (.cancelled _ _) _ => .cancelled
  | .fullPrecompile (.returned step) => classifyPrecompileStep machine step
  | .fullPrecompile .outOfGas =>
      if topLevel machine then .fullPrecompileOutOfGasTop
      else .fullPrecompileOutOfGasNested
  | .fullPrecompile (.returnedFailure _) =>
      if topLevel machine then .fullPrecompileReturnedFailureTop
      else .fullPrecompileReturnedFailureNested
  | .fullPrecompile (.managedException _) =>
      if topLevel machine then .fullPrecompileManagedExceptionTop
      else .fullPrecompileManagedExceptionNested
  | .fullPrecompile (.escaped _) => .escaped

def settleInvocation (leaves : DriverLeaves) (route : SettlementRoute)
    (invocation : Invocation) (machine : Machine) : DriverResult :=
  settle leaves route invocation machine

def cleanup (leaves : DriverLeaves) (result : DriverResult) : DriverResult :=
  cleanupTerminal leaves result

def stepOnceBody (leaves : DriverLeaves) (machine : Machine) : DriverResult :=
  let prepared := prepare leaves machine
  let invocation := dispatch leaves prepared
  let route := classifyInvocation prepared invocation
  let settled := settleInvocation leaves route invocation prepared
  let observed := { settled with directInlineStaticPrecompile := invocation.directInline }
  cleanup leaves observed

def executePlan (leaves : DriverLeaves) (machine : Machine)
    (plan : List ControlPlanStep) : DriverResult :=
  match plan with
  | { stage := ControlPlanStage.prepare, member := "ExecuteTransaction", nodeId := "n005", kind := "if", sourceArm := "body", sourceSha256 := "dad25b4c5bed157bd5ddf8f4589cfc0624296b5634590fa7a29323872987928e" } ::
      { stage := ControlPlanStage.dispatch, member := "ExecuteTransaction", nodeId := "n010", kind := "if", sourceArm := "try", sourceSha256 := "a53c5b8066ee6cf3ab56a4ebb42723b3fbfb7e359b0b5b774c036470f671becf" } ::
      { stage := ControlPlanStage.classify, member := "ExecuteTransaction", nodeId := "n020", kind := "if", sourceArm := "else", sourceSha256 := "1367fcf2e1261bc1e057d1751723da2ccb8a702313d2819a26a8180e53c077be" } ::
      { stage := ControlPlanStage.settle, member := "ExecuteTransaction", nodeId := "n045", kind := "statement", sourceArm := "else", sourceSha256 := "ad28cd96d64b9aeebc155ae6e64da48e07a3521838acc0644cb064c232ba639b" } ::
      { stage := ControlPlanStage.cleanup, member := "Dispose", nodeId := "n001", kind := "statement", sourceArm := "then", sourceSha256 := "87c615227e527913f71472eceafc45a6d4fcd3c81b6856aa280803900cc6b7d6" } :: [] =>

      stepOnceBody leaves machine
  | _ => DriverResult.incomplete machine

def stepOnce (leaves : DriverLeaves) (machine : Machine) : DriverResult :=
  if sourceControlReady && controlTopologyReady && controlPlanReady && phaseReady machine then
    executePlan leaves machine admittedControlPlan
  else DriverResult.incomplete machine

def runOne (fuel : Nat) (leaves : DriverLeaves) (machine : Machine) : FuelResult :=
  match fuel with
  | 0 => .exhausted machine
  | _ + 1 => .stepped (stepOnce leaves machine)

end Eip803x.Evm.FrameDriver.Generated
