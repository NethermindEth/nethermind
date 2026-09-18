// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Evm.Lean.OrdinaryTransactionRefundAdapterExtractor;

internal sealed class AdmissionException(string message) : Exception(message);

internal enum RefundEntry
{
    OrdinaryRefund,
    PreparationOutOfGas,
    CreateStateOutOfGas,
    ContractCollision,
    FailedDeposit,
}

internal sealed record GasState(ulong Value, long StateReservoir, long StateGasUsed, long StateGasSpill, long StateGasSpillRefunded);

internal sealed record RefundObservations(
    RefundEntry Entry,
    ulong TransactionGasLimit,
    BigInteger GasPrice,
    BigInteger MaxFeePerGas,
    BigInteger MaxPriorityFeePerGas,
    bool SkipValidation,
    bool IsContractCreation,
    bool IsEip8037Enabled,
    bool IsEip3529Enabled,
    bool IsEip7778Enabled,
    bool IsError,
    bool ShouldRevert,
    long RefundCounter,
    int DestroyCount,
    ulong DestroyRefund,
    ulong CodeInsertRefundCount,
    GasState IncomingGas,
    GasState IntrinsicStandard,
    GasState FloorGas,
    long PostIntrinsicStateReservoir,
    bool TopLevelCreateStateGasCharged);

internal sealed record SettlementInput(
    ulong TransactionGasLimit,
    ulong PreRefundGas,
    long RefundCounter,
    int DestroyCount,
    ulong DestroyRefund,
    ulong CodeInsertExecutionRefund,
    ulong CalldataFloorGas,
    long StateGasUsed,
    ulong RefundQuotient,
    bool IsError,
    bool ShouldRevert,
    bool IsEip8037Enabled,
    bool IsEip7778Enabled);

internal sealed record ConsumedGas(ulong SpentGas, ulong OperationGas, ulong BlockGas, ulong BlockStateGas, ulong MaxUsedGas, ulong GasRefund);

internal sealed record RefundResult(
    GasState WorkingGas,
    GasState CallerGas,
    SettlementInput? Settlement,
    ConsumedGas Consumed,
    bool PayRefundCalled,
    BigInteger PaymentAmount,
    BigInteger? SenderCredit,
    long? HaltStateFloor);

internal sealed record SourceIdentity(string Path, string Role, string Sha256);
internal sealed record ReferenceIdentity(string Path, string AssemblyName, string Sha256, string Mvid, bool Selected);
internal sealed record ReferenceInventory(int SchemaVersion, int Count, ReferenceIdentity[] References);
internal sealed record ParameterIdentity(string Name, int Ordinal, string Type, string RefKind);
internal sealed record MemberIdentity(string Symbol, string MethodKind, string ReturnType, ParameterIdentity[] Parameters, string Path, int Start, int Length, string SyntaxSha256);

/// <summary>Retains the typed operation tree, including implicit conversions and argument roles.</summary>
internal sealed record OperationTerm(
    string Kind,
    string Type,
    bool Implicit,
    string Symbol,
    string Operator,
    string Constant,
    string ArgumentKind,
    int ParameterOrdinal,
    string RefKind,
    string Conversion,
    OperationTerm[] Children);

internal sealed record AdmittedMember(MemberIdentity Identity, OperationTerm Body);
internal sealed record CompilerAdmission(
    string AssemblyName,
    string LanguageVersion,
    string[] DefineConstants,
    string ReferenceInventorySha256,
    ReferenceIdentity[] References,
    SourceIdentity[] Sources,
    AdmittedMember[] Members);

internal sealed record RefundConstants(ulong ExecutionCap, long CreateStateCost, ulong NewAccountCost, ulong PerAuthorizationCost, ulong LegacyRefundQuotient, ulong Eip3529RefundQuotient);

internal enum ValueType { Bool, UInt64, Int64, Int32, Int128, UInt256, Gas, Input, Constants, Consumed }
internal enum ExpressionKind { Variable, Literal, Projection, Convert, Not, And, Or, Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, Add, Subtract, Multiply, Divide, Conditional, Min, Max, PreRefundGas, ShouldRefundGas, ShouldValidateGas, ClaimableRefund, SaturatingSubtract }
internal sealed record RefundExpression(ExpressionKind Kind, ValueType Type, string Value, RefundExpression[] Arguments);
internal sealed record ExpressionSite(string Role, string Owner, string CanonicalSyntax, OperationTerm Operation, RefundExpression Expression);
internal enum HaltStage { RefundState, ResetState, ClearExecution }
internal sealed record RefundPlan(ExpressionSite[] Expressions, HaltStage[] HaltStages);
