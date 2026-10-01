// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal enum StageBValueKind { Unit, Null, Bool, UInt64, Int64, UInt256, Enum, Address, Bytes, Reference, Struct }

internal sealed record StageBValue
{
    private StageBValue(StageBValueKind kind, string type, bool boolean = false, ulong unsigned = 0, long signed = 0,
        BigInteger integer = default, string? text = null, byte[]? bytes = null, IReadOnlyDictionary<string, StageBValue>? fields = null)
    {
        Kind = kind;
        Type = type;
        Boolean = boolean;
        Unsigned = unsigned;
        Signed = signed;
        Integer = integer;
        Text = text;
        Bytes = bytes;
        Fields = fields;
    }

    internal StageBValueKind Kind { get; }
    internal string Type { get; }
    internal bool Boolean { get; }
    internal ulong Unsigned { get; }
    internal long Signed { get; }
    internal BigInteger Integer { get; }
    internal string? Text { get; }
    internal byte[]? Bytes { get; }
    internal IReadOnlyDictionary<string, StageBValue>? Fields { get; }

    internal static StageBValue Unit { get; } = new(StageBValueKind.Unit, "void");
    internal static StageBValue Null(string type) => new(StageBValueKind.Null, type);
    internal static StageBValue Bool(bool value) => new(StageBValueKind.Bool, "bool", boolean: value);
    internal static StageBValue UInt64(ulong value) => new(StageBValueKind.UInt64, "ulong", unsigned: value);
    internal static StageBValue Int64(long value) => new(StageBValueKind.Int64, "long", signed: value);
    internal static StageBValue UInt256(BigInteger value)
    {
        if (value < 0 || value >= BigInteger.One << 256) throw new ArgumentOutOfRangeException(nameof(value));
        return new(StageBValueKind.UInt256, "global::Nethermind.Int256.UInt256", integer: value);
    }
    internal static StageBValue Enum(string type, long value) => new(StageBValueKind.Enum, type, signed: value);
    internal static StageBValue Address(string value) => new(StageBValueKind.Address, "global::Nethermind.Core.Address", text: value);
    internal static StageBValue BytesValue(byte[] value) => new(StageBValueKind.Bytes, "global::System.ReadOnlyMemory<byte>", bytes: value.ToArray());
    internal static StageBValue Reference(string type, string identity) => new(StageBValueKind.Reference, type, text: identity);
    internal static StageBValue Struct(string type, params (string Name, StageBValue Value)[] fields) =>
        new(StageBValueKind.Struct, type, fields: fields.ToDictionary(static field => field.Name, static field => field.Value, StringComparer.Ordinal));

    internal StageBValue Field(string name) => Fields is not null && Fields.TryGetValue(name, out StageBValue? value)
        ? value : throw new StageBExecutionException("missing-field", $"{Type}.{name}");

    internal StageBValue WithField(string name, StageBValue value)
    {
        if (Kind != StageBValueKind.Struct) throw new StageBExecutionException("field-write", Type);
        Dictionary<string, StageBValue> fields = Fields is null ? new(StringComparer.Ordinal) : new(Fields, StringComparer.Ordinal);
        fields[name] = value;
        return new(StageBValueKind.Struct, Type, fields: fields);
    }

    internal bool StructurallyEquals(StageBValue? other)
    {
        if (other is null || Kind != other.Kind || Type != other.Type || Boolean != other.Boolean ||
            Unsigned != other.Unsigned || Signed != other.Signed || Integer != other.Integer || Text != other.Text)
            return false;
        if (Bytes is not null || other.Bytes is not null)
            return Bytes is not null && other.Bytes is not null && Bytes.AsSpan().SequenceEqual(other.Bytes);
        if (Fields is null || other.Fields is null) return Fields is null && other.Fields is null;
        return Fields.Count == other.Fields.Count && Fields.All(field =>
            other.Fields.TryGetValue(field.Key, out StageBValue? value) && field.Value.StructurallyEquals(value));
    }
}

internal sealed record StageBGasValue(ulong Value, long StateReservoir, long StateGasUsed, long StateGasSpill, long StateGasSpillRefunded)
{
    internal StageBValue ToValue() => StageBValue.Struct("global::Nethermind.Evm.GasPolicy.EthereumGasPolicy",
        (nameof(Value), StageBValue.UInt64(Value)),
        (nameof(StateReservoir), StageBValue.Int64(StateReservoir)),
        (nameof(StateGasUsed), StageBValue.Int64(StateGasUsed)),
        (nameof(StateGasSpill), StageBValue.Int64(StateGasSpill)),
        (nameof(StateGasSpillRefunded), StageBValue.Int64(StateGasSpillRefunded)));
}

internal sealed record StageBIntrinsicGasInput(StageBGasValue Standard, StageBGasValue FloorGas)
{
    internal StageBValue ToValue() => StageBValue.Struct("global::Nethermind.Evm.GasPolicy.IntrinsicGas<TGasPolicy>",
        (nameof(Standard), Standard.ToValue()), (nameof(FloorGas), FloorGas.ToValue()));
}

internal sealed record StageBTransactionInput(string Sender, string? Recipient, BigInteger Value, byte[] Data, ulong GasLimit,
    bool HasAuthorizationList = false);
internal sealed record StageBSpecInput(bool IsEip8037Enabled, bool IsEip7708Enabled);
internal sealed record StageBTracerInput(bool IsTracingState = false, bool IsTracingActions = false, bool IsTracingCode = false, bool IsTracingLogs = false,
    bool IsTracingAccess = false);

/// <summary>Raw post-nonce inputs. Derived gas state and Refund operands are deliberately absent.</summary>
internal sealed record StageBPostNonceInput(
    StageBTransactionInput Transaction,
    StageBSpecInput Spec,
    StageBTracerInput Tracer,
    StageBIntrinsicGasInput IntrinsicGas,
    bool Restore,
    bool Commit,
    bool DeleteCallerAccount,
    bool Warmup,
    BigInteger OpcodeGasPrice,
    BigInteger PremiumPerGas,
    BigInteger SenderReservedGasPayment,
    BigInteger BlobBaseFee,
    ulong ExecutionGasLimitCap,
    long NewAccountStateCost,
    bool IsCodeOverridable = false,
    bool ForceSimpleTransferDisabled = false)
{
    internal void Validate()
    {
        if (Transaction.Recipient is null) throw new StageBExecutionException("input-domain", "contract creation");
        if (!Commit || Restore) throw new StageBExecutionException("input-domain", "selected scope requires commit and no restore");
        if (Tracer.IsTracingState) throw new StageBExecutionException("unsupported-input", "state tracing");
        if (Spec.IsEip7708Enabled) throw new StageBExecutionException("unsupported-input", "EIP-7708 transfer log");
        if (Warmup) throw new StageBExecutionException("unsupported-input", "warmup balance truncation");
        if (IntrinsicGas.Standard.StateReservoir < 0)
            throw new StageBExecutionException("input-domain", "negative intrinsic state gas");
        if (ExecutionGasLimitCap != 16_777_216 || NewAccountStateCost != 183_600)
            throw new StageBExecutionException("input-domain", "pinned production gas constants");
        ulong intrinsicTotal = unchecked(IntrinsicGas.Standard.Value + unchecked((ulong)IntrinsicGas.Standard.StateReservoir));
        if (Transaction.GasLimit < intrinsicTotal)
            throw new StageBExecutionException("unsupported-input", "initialization entry-domain exclusion");
        _ = StageBValue.UInt256(Transaction.Value);
        _ = StageBValue.UInt256(OpcodeGasPrice);
        _ = StageBValue.UInt256(PremiumPerGas);
        _ = StageBValue.UInt256(SenderReservedGasPayment);
        _ = StageBValue.UInt256(BlobBaseFee);
    }
}

internal abstract record StageBRequest;
internal sealed record StageBCodeLookupRequest(string Recipient, bool FollowDelegation) : StageBRequest;
internal sealed record StageBIsDeadAccountRequest(string Recipient) : StageBRequest;
internal sealed record StageBSubtractBalanceRequest(string Address, BigInteger Amount) : StageBRequest;
internal sealed record StageBAddBalanceRequest(string Address, BigInteger Amount) : StageBRequest;
internal sealed record StageBCommitRequest(bool TracingState, bool CommitRoots) : StageBRequest;
internal sealed record StageBTraceRequest(string Operation, StageBValue[] Arguments) : StageBRequest;

internal abstract record StageBExchange(StageBRequest Expected);
internal sealed record StageBCodeLookupExchange(StageBRequest Request, bool IsEmpty, string? DelegationAddress) : StageBExchange(Request);
internal sealed record StageBBoolExchange(StageBRequest Request, bool Value) : StageBExchange(Request);
internal sealed record StageBUnitExchange(StageBRequest Request) : StageBExchange(Request);

internal sealed class StageBResponseTape(params StageBExchange[] exchanges)
{
    private readonly StageBExchange[] _exchanges = exchanges;
    private int _position;
    internal int Consumed => _position;
    internal int Remaining => _exchanges.Length - _position;
    internal StageBExchange[] RemainingExchanges => _exchanges[_position..];

    internal T Take<T>(StageBRequest request) where T : StageBExchange
    {
        if (_position >= _exchanges.Length)
            throw new StageBExecutionException("response-tape", $"missing {request.GetType().Name}", StageBRejection.TapeEmpty);
        StageBExchange exchange = _exchanges[_position];
        if (!RequestEquals(exchange.Expected, request))
            throw new StageBExecutionException("response-tape", $"expected {exchange.Expected}, observed {request}", StageBRejection.TapeRequest);
        if (exchange is not T typed)
            throw new StageBExecutionException("response-tape", $"wrong reply for {request}", StageBRejection.TapeReply);
        _position++;
        return typed;
    }

    private static bool RequestEquals(StageBRequest expected, StageBRequest observed) =>
        expected is StageBTraceRequest left && observed is StageBTraceRequest right
            ? left.Operation == right.Operation && left.Arguments.Length == right.Arguments.Length &&
                left.Arguments.Zip(right.Arguments).All(static pair => pair.First.StructurallyEquals(pair.Second))
            : expected == observed;
}

internal sealed record StageBResolvedLocation(object Root, string[] Fields, bool ReadOnly, string Provenance);
internal sealed record StageBRefundOperand(int Ordinal, StageBArgumentMode Mode, StageBValue Value, string Location,
    StageBResolvedLocation? ResolvedLocation = null);
internal sealed record StageBRefundSuspension(StageBRefundOperand[] Operands, StageBGasValue GasAvailable, long PostIntrinsicStateReservoir,
    StageBValue Substate, StageBRequest[] Requests, long RemainingFuel);

internal enum StageBRunOutcomeKind { RefundSuspended, Returned, OutsideSelectedDomain, Rejected, Fault, FuelExhausted }
internal enum StageBRejection { None, InputDomain, FuelDomain, TapeEmpty, TapeRequest, TapeReply, Unexpected }
internal sealed record StageBPrefixRun(StageBRunOutcomeKind Kind, StageBRefundSuspension? Suspension, StageBValue? ReturnValue,
    string Diagnostic, StageBRequest[] Requests, long RemainingFuel = 0, StageBRejection Rejection = StageBRejection.None);

internal sealed class StageBExecutionException(string code, string detail, StageBRejection rejection = StageBRejection.Unexpected)
    : Exception($"Stage-B execution {code}: {detail}.")
{
    internal string Code { get; } = code;
    internal StageBRejection Rejection { get; } = rejection;
}
