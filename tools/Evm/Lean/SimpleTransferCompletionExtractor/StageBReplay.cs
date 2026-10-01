// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal sealed record StageBReplayCase(string Id, long Fuel, long MicroFuel, StageBPostNonceInput Input, StageBExchange[] Tape);

internal static class StageBReplay
{
    internal const string Schema = "stage-b-replay-v2";
    internal const int MaxLineLength = 1_048_576;
    internal const int MaxTapeLength = 4096;

    internal static StageBReplayCase Decode(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaxLineLength) throw Invalid("line length");
        using JsonDocument document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement row = Array(document.RootElement, 7);
        if (Text(row[0]) != Schema || Text(row[2]) != StageBArtifact.ExpectedPrefixIntegrity)
            throw Invalid("schema or program identity");
        JsonElement fields = Array(row[5], 16);
        JsonElement tx = Array(fields[0], 6);
        JsonElement spec = Array(fields[1], 2);
        JsonElement tracer = Array(fields[2], 5);
        JsonElement intrinsic = Array(fields[3], 2);
        StageBPostNonceInput input = new(
            new(Text(tx[0]), OptionalText(tx[1]), Unsigned(tx[2], 256), Bytes(tx[3]), (ulong)Unsigned(tx[4], 64), Bool(tx[5])),
            new(Bool(spec[0]), Bool(spec[1])), new(Bool(tracer[0]), Bool(tracer[1]), Bool(tracer[2]), Bool(tracer[3]), Bool(tracer[4])),
            new(Gas(intrinsic[0]), Gas(intrinsic[1])), Bool(fields[4]), Bool(fields[5]), Bool(fields[6]), Bool(fields[7]),
            Unsigned(fields[8], 256), Unsigned(fields[9], 256), Unsigned(fields[10], 256), Unsigned(fields[11], 256),
            (ulong)Unsigned(fields[14], 64), Signed(fields[15]), Bool(fields[12]), Bool(fields[13]));
        JsonElement tape = Array(row[6]);
        if (tape.GetArrayLength() > MaxTapeLength) throw Invalid("tape length");
        StageBExchange[] exchanges = new StageBExchange[tape.GetArrayLength()];
        for (int index = 0; index < exchanges.Length; index++) exchanges[index] = Exchange(tape[index]);
        long fuel = (long)Unsigned(row[3], 63);
        long microFuel = (long)Unsigned(row[4], 63);
        if (microFuel > 1_000_000) throw Invalid("micro-fuel limit");
        return new(Text(row[1]), fuel, microFuel, input, exchanges);
    }

    internal static string Encode(StageBReplayCase replay) => A(Schema, replay.Id, StageBArtifact.ExpectedPrefixIntegrity,
        N(replay.Fuel), N(replay.MicroFuel), Input(replay.Input), new JsonArray(replay.Tape.Select(Exchange).ToArray())).ToJsonString();

    internal static bool CommonDomain(StageBPostNonceInput input)
    {
        StageBIntrinsicGasInput intrinsic = input.IntrinsicGas;
        return input.Transaction.Recipient is not null && !input.Transaction.HasAuthorizationList &&
            input.Spec.IsEip8037Enabled && !input.Spec.IsEip7708Enabled && !input.Tracer.IsTracingState &&
            !input.Tracer.IsTracingActions && !input.Tracer.IsTracingCode && !input.Tracer.IsTracingLogs &&
            !input.Restore && input.Commit && !input.Warmup && !input.IsCodeOverridable && !input.ForceSimpleTransferDisabled &&
            IsUInt256(input.Transaction.Value) && IsUInt256(input.OpcodeGasPrice) && IsUInt256(input.PremiumPerGas) &&
            IsUInt256(input.SenderReservedGasPayment) && IsUInt256(input.BlobBaseFee) &&
            input.ExecutionGasLimitCap == 16_777_216 && input.NewAccountStateCost == 183_600 &&
            input.Transaction.GasLimit <= long.MaxValue && intrinsic.Standard.StateReservoir >= 0 &&
            intrinsic.Standard.Value <= input.ExecutionGasLimitCap &&
            (BigInteger)input.Transaction.GasLimit >= (BigInteger)intrinsic.Standard.Value + intrinsic.Standard.StateReservoir;
    }

    internal static JsonArray Run(StageBPrefixProgram program, StageBReplayCase replay)
    {
        StageBResponseTape tape = new(replay.Tape);
        StageBPrefixRun run;
        if (program.Integrity != StageBArtifact.ExpectedPrefixIntegrity)
            throw Invalid("program identity");
        if (replay.Fuel <= 0 || replay.Fuel > program.FuelBound)
            run = new(StageBRunOutcomeKind.Rejected, null, null, "", [], replay.Fuel, StageBRejection.FuelDomain);
        else if (!CommonDomain(replay.Input))
            run = new(StageBRunOutcomeKind.Rejected, null, null, "", [], replay.Fuel, StageBRejection.InputDomain);
        else
            run = StageBPrefixInterpreter.Run(program, replay.Input, tape, replay.Fuel);
        if (run.Kind == StageBRunOutcomeKind.Fault || run.Rejection == StageBRejection.Unexpected)
            throw Invalid("unexpected interpreter failure: " + run.Diagnostic);
        if (run.Suspension is { } suspension &&
            (suspension.Operands.Length != 12 || suspension.RemainingFuel != run.RemainingFuel ||
             !suspension.Operands.Select(static operand => operand.Ordinal).SequenceEqual(Enumerable.Range(0, 12))))
            throw Invalid("suspension shape");

        Dictionary<object, int> roots = new(ReferenceEqualityComparer.Instance);
        JsonArray operands = [];
        foreach (StageBRefundOperand operand in run.Suspension?.Operands ?? [])
        {
            JsonNode? location = null;
            if (operand.ResolvedLocation is { } resolved)
            {
                if (!roots.TryGetValue(resolved.Root, out int root))
                {
                    root = roots.Count;
                    roots.Add(resolved.Root, root);
                }
                location = A(N(root), new JsonArray(resolved.Fields.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                    resolved.ReadOnly, resolved.Provenance);
            }
            string mode = operand.Mode switch
            {
                StageBArgumentMode.Value => "value",
                StageBArgumentMode.ReadOnlyLocation => "readonly-location",
                _ => throw Invalid("unsupported operand mode"),
            };
            operands.Add(A(N(operand.Ordinal), mode, Value(operand.Value), location));
        }
        string tag = run.Kind switch
        {
            StageBRunOutcomeKind.RefundSuspended => "suspended",
            StageBRunOutcomeKind.Returned => "returned",
            StageBRunOutcomeKind.OutsideSelectedDomain => "outside",
            StageBRunOutcomeKind.Rejected => "rejected",
            StageBRunOutcomeKind.FuelExhausted => "fuel-exhausted",
            _ => throw Invalid("unsupported outcome"),
        };
        string? rejection = run.Rejection switch
        {
            StageBRejection.None => null,
            StageBRejection.InputDomain => "input-domain",
            StageBRejection.FuelDomain => "fuel-domain",
            StageBRejection.TapeEmpty => "tape-empty",
            StageBRejection.TapeRequest => "tape-request",
            StageBRejection.TapeReply => "tape-reply",
            _ => throw Invalid("unsupported rejection"),
        };
        return A(Schema, replay.Id, StageBArtifact.ExpectedPrefixIntegrity, tag, rejection, N(run.RemainingFuel),
            new JsonArray(run.Requests.Select(Request).ToArray()), new JsonArray(tape.RemainingExchanges.Select(Exchange).ToArray()),
            run.ReturnValue is null ? null : Value(run.ReturnValue), operands);
    }

    internal static bool SameObservation(JsonNode left, JsonNode right) => JsonNode.DeepEquals(left, right);

    private static JsonArray Input(StageBPostNonceInput value) => A(
        A(value.Transaction.Sender, value.Transaction.Recipient, N(value.Transaction.Value), Convert.ToHexStringLower(value.Transaction.Data),
            N(value.Transaction.GasLimit), value.Transaction.HasAuthorizationList),
        A(value.Spec.IsEip8037Enabled, value.Spec.IsEip7708Enabled),
        A(value.Tracer.IsTracingState, value.Tracer.IsTracingActions, value.Tracer.IsTracingCode, value.Tracer.IsTracingLogs, value.Tracer.IsTracingAccess),
        A(Gas(value.IntrinsicGas.Standard), Gas(value.IntrinsicGas.FloorGas)), value.Restore, value.Commit, value.DeleteCallerAccount,
        value.Warmup, N(value.OpcodeGasPrice), N(value.PremiumPerGas), N(value.SenderReservedGasPayment), N(value.BlobBaseFee),
        value.IsCodeOverridable, value.ForceSimpleTransferDisabled, N(value.ExecutionGasLimitCap), N(value.NewAccountStateCost));

    private static StageBGasValue Gas(JsonElement value)
    {
        JsonElement row = Array(value, 5);
        return new((ulong)Unsigned(row[0], 64), Signed(row[1]), Signed(row[2]), Signed(row[3]), Signed(row[4]));
    }

    private static JsonArray Gas(StageBGasValue value) => A(N(value.Value), N(value.StateReservoir), N(value.StateGasUsed),
        N(value.StateGasSpill), N(value.StateGasSpillRefunded));

    private static StageBExchange Exchange(JsonElement value)
    {
        JsonElement row = Array(value, 2);
        StageBRequest request = Request(row[0]);
        JsonElement reply = Array(row[1]);
        return Text(reply[0]) switch
        {
            "code" when reply.GetArrayLength() == 3 => new StageBCodeLookupExchange(request, Bool(reply[1]), OptionalText(reply[2])),
            "bool" when reply.GetArrayLength() == 2 => new StageBBoolExchange(request, Bool(reply[1])),
            "unit" when reply.GetArrayLength() == 1 => new StageBUnitExchange(request),
            _ => throw Invalid("reply shape"),
        };
    }

    private static JsonNode Exchange(StageBExchange value) => A(Request(value.Expected), value switch
    {
        StageBCodeLookupExchange code => A("code", code.IsEmpty, code.DelegationAddress),
        StageBBoolExchange boolean => A("bool", boolean.Value),
        StageBUnitExchange => A("unit"),
        _ => throw Invalid("reply kind"),
    });

    private static StageBRequest Request(JsonElement value)
    {
        JsonElement row = Array(value);
        return Text(row[0]) switch
        {
            "code" when row.GetArrayLength() == 3 => new StageBCodeLookupRequest(Text(row[1]), Bool(row[2])),
            "empty-calls" when row.GetArrayLength() == 1 => new StageBTraceRequest("IncrementEmptyCalls", []),
            "dead" when row.GetArrayLength() == 2 => new StageBIsDeadAccountRequest(Text(row[1])),
            "subtract" when row.GetArrayLength() == 3 => new StageBSubtractBalanceRequest(Text(row[1]), Unsigned(row[2], 256)),
            "add" when row.GetArrayLength() == 3 => new StageBAddBalanceRequest(Text(row[1]), Unsigned(row[2], 256)),
            "commit" when row.GetArrayLength() == 3 => new StageBCommitRequest(Bool(row[1]), Bool(row[2])),
            _ => throw Invalid("request shape"),
        };
    }

    private static JsonNode Request(StageBRequest value) => value switch
    {
        StageBCodeLookupRequest code => A("code", code.Recipient, code.FollowDelegation),
        StageBTraceRequest { Operation: "IncrementEmptyCalls", Arguments.Length: 0 } => A("empty-calls"),
        StageBIsDeadAccountRequest dead => A("dead", dead.Recipient),
        StageBSubtractBalanceRequest subtract => A("subtract", subtract.Address, N(subtract.Amount)),
        StageBAddBalanceRequest add => A("add", add.Address, N(add.Amount)),
        StageBCommitRequest commit => A("commit", commit.TracingState, commit.CommitRoots),
        _ => throw Invalid("request kind"),
    };

    private static JsonNode Value(StageBValue value) => value.Kind switch
    {
        StageBValueKind.Unit => A("unit"),
        StageBValueKind.Null => A("null", value.Type),
        StageBValueKind.Bool => A("bool", value.Boolean),
        StageBValueKind.UInt64 => A("ulong", N(value.Unsigned)),
        StageBValueKind.Int64 => A("long", N(value.Signed)),
        StageBValueKind.UInt256 => A("uint256", N(value.Integer)),
        StageBValueKind.Enum => A("enum", value.Type, N(value.Signed)),
        StageBValueKind.Address => A("address", value.Text),
        StageBValueKind.Bytes => A("bytes", Convert.ToHexStringLower(value.Bytes!)),
        StageBValueKind.Reference => A("reference", value.Type, value.Text),
        StageBValueKind.Struct => A("struct", value.Type, new JsonArray(value.Fields!.OrderBy(static field => field.Key, StringComparer.Ordinal)
            .Select(static field => (JsonNode?)A(field.Key, Value(field.Value))).ToArray())),
        _ => throw Invalid("value kind"),
    };

    private static JsonElement Array(JsonElement value, int? length = null)
    {
        if (value.ValueKind != JsonValueKind.Array || (length is { } count && value.GetArrayLength() != count))
            throw Invalid("array shape");
        return value;
    }

    private static string Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid("string required");
        string text = value.GetString()!;
        for (int index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;
            if (!char.IsHighSurrogate(text[index]) || index + 1 >= text.Length || !char.IsLowSurrogate(text[++index]))
                throw Invalid("Unicode scalar required");
        }
        return text;
    }

    private static string? OptionalText(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Text(value);
    private static bool Bool(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw Invalid("boolean required");
    private static BigInteger Unsigned(JsonElement value, int bits)
    {
        string text = Text(value);
        if (!BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out BigInteger number) || number < 0 ||
            number >= BigInteger.One << bits || text != N(number)) throw Invalid("unsigned decimal");
        return number;
    }
    private static long Signed(JsonElement value)
    {
        string text = Text(value);
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) || text != N(number))
            throw Invalid("signed decimal");
        return number;
    }
    private static byte[] Bytes(JsonElement value)
    {
        string text = Text(value);
        if (text.Length % 2 != 0 || text.Any(static c => !char.IsAsciiHexDigitLower(c))) throw Invalid("lowercase byte hex");
        return Convert.FromHexString(text);
    }
    private static bool IsUInt256(BigInteger value) => value >= 0 && value < BigInteger.One << 256;
    private static string N(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
    private static JsonArray A(params JsonNode?[] values) => [.. values];
    private static ArgumentException Invalid(string reason) => new("Stage-B replay: " + reason);
}
