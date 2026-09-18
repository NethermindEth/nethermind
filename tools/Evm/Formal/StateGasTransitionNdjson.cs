// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Evm.GasPolicy;

namespace Evm.Formal;

internal static class StateGasTransitionNdjson
{
    private const string CurrentSchemaVersion = "1";
    private const string Operation = "state-transition";
    private const int InvalidInputExitCode = 2;
    private const string SchemaVersionProperty = "schemaVersion";
    private const string OperationProperty = "operation";
    private const string InputProperty = "input";
    private const string TransitionProperty = "transition";
    private const string GasLeftProperty = "gasLeft";
    private const string StateReservoirProperty = "stateReservoir";
    private const string StateGasUsedProperty = "stateGasUsed";
    private const string StateGasSpillProperty = "stateGasSpill";
    private const string StateGasSpillRefundedProperty = "stateGasSpillRefunded";
    private const string ChildGasLeftProperty = "childGasLeft";
    private const string ChildStateReservoirProperty = "childStateReservoir";
    private const string ChildStateGasUsedProperty = "childStateGasUsed";
    private const string ChildStateGasSpillProperty = "childStateGasSpill";
    private const string ChildStateGasSpillRefundedProperty = "childStateGasSpillRefunded";
    private const string AmountProperty = "amount";
    private const string StateGasFloorProperty = "stateGasFloor";
    private const string TrackSpillRefundProperty = "trackSpillRefund";

    private const string RefundTransition = "refund";
    private const string RepayStateGasSpillTransition = "repayStateGasSpill";
    private const string RestoreChildStateGasTransition = "restoreChildStateGas";
    private const string RestoreChildStateGasOnHaltTransition = "restoreChildStateGasOnHalt";
    private const string RevertRefundToHaltTransition = "revertRefundToHalt";
    private const string RefundStateGasTransition = "refundStateGas";
    private const string DiscardStateGasTransition = "discardStateGas";
    private const string AddStateGasRefundToReservoirTransition = "addStateGasRefundToReservoir";
    private const string RemoveStateGasRefundFromReservoirTransition = "removeStateGasRefundFromReservoir";

    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static int Run(TextReader input, TextWriter output, TextWriter error)
    {
        int lineNumber = 0;
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            lineNumber++;
            try
            {
                StateGasTransitionRequest request = ParseRequest(line);
                StateGasTransitionResponse response = ExecuteProductionAdapter(request);
                output.WriteLine(JsonSerializer.Serialize(response, OutputJsonOptions));
            }
            catch (JsonException exception)
            {
                return Fail(error, lineNumber, $"invalid JSON: {exception.Message}");
            }
            catch (StateGasTransitionInputException exception)
            {
                return Fail(error, lineNumber, exception.Message);
            }
        }

        return 0;
    }

    private static StateGasTransitionRequest ParseRequest(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new StateGasTransitionInputException("the JSON root must be an object.");
        }

        ValidateEnvelopeProperties(root);
        string schemaVersion = ReadRequiredString(root, SchemaVersionProperty, "root");
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new StateGasTransitionInputException($"{SchemaVersionProperty} must be \"{CurrentSchemaVersion}\".");
        }

        string operation = ReadRequiredString(root, OperationProperty, "root");
        if (operation != Operation)
        {
            throw new StateGasTransitionInputException($"{OperationProperty} must be \"{Operation}\".");
        }

        JsonElement input = ReadRequiredObject(root, InputProperty, "root");
        return ParseInput(input);
    }

    private static StateGasTransitionRequest ParseInput(JsonElement input)
    {
        bool hasTransition = false;
        string? transition = null;
        bool hasGasLeft = false;
        ulong gasLeft = 0;
        bool hasStateReservoir = false;
        long stateReservoir = 0;
        bool hasStateGasUsed = false;
        long stateGasUsed = 0;
        bool hasStateGasSpill = false;
        long stateGasSpill = 0;
        bool hasStateGasSpillRefunded = false;
        long stateGasSpillRefunded = 0;
        bool hasChildGasLeft = false;
        ulong childGasLeft = 0;
        bool hasChildStateReservoir = false;
        long childStateReservoir = 0;
        bool hasChildStateGasUsed = false;
        long childStateGasUsed = 0;
        bool hasChildStateGasSpill = false;
        long childStateGasSpill = 0;
        bool hasChildStateGasSpillRefunded = false;
        long childStateGasSpillRefunded = 0;
        bool hasAmount = false;
        long amount = 0;
        bool hasStateGasFloor = false;
        long stateGasFloor = 0;
        bool hasTrackSpillRefund = false;
        bool trackSpillRefund = false;

        foreach (JsonProperty property in input.EnumerateObject())
        {
            switch (property.Name)
            {
                case TransitionProperty when !hasTransition:
                    hasTransition = true;
                    transition = ReadString(property.Value, $"{InputProperty}.{TransitionProperty}");
                    break;
                case TransitionProperty:
                    throw DuplicateInputProperty(property.Name);
                case GasLeftProperty when !hasGasLeft:
                    hasGasLeft = true;
                    gasLeft = ParseUInt64(property.Value, $"{InputProperty}.{GasLeftProperty}");
                    break;
                case GasLeftProperty:
                    throw DuplicateInputProperty(property.Name);
                case StateReservoirProperty when !hasStateReservoir:
                    hasStateReservoir = true;
                    stateReservoir = ParseInt64(property.Value, $"{InputProperty}.{StateReservoirProperty}");
                    break;
                case StateReservoirProperty:
                    throw DuplicateInputProperty(property.Name);
                case StateGasUsedProperty when !hasStateGasUsed:
                    hasStateGasUsed = true;
                    stateGasUsed = ParseInt64(property.Value, $"{InputProperty}.{StateGasUsedProperty}");
                    break;
                case StateGasUsedProperty:
                    throw DuplicateInputProperty(property.Name);
                case StateGasSpillProperty when !hasStateGasSpill:
                    hasStateGasSpill = true;
                    stateGasSpill = ParseInt64(property.Value, $"{InputProperty}.{StateGasSpillProperty}");
                    break;
                case StateGasSpillProperty:
                    throw DuplicateInputProperty(property.Name);
                case StateGasSpillRefundedProperty when !hasStateGasSpillRefunded:
                    hasStateGasSpillRefunded = true;
                    stateGasSpillRefunded = ParseInt64(property.Value, $"{InputProperty}.{StateGasSpillRefundedProperty}");
                    break;
                case StateGasSpillRefundedProperty:
                    throw DuplicateInputProperty(property.Name);
                case ChildGasLeftProperty when !hasChildGasLeft:
                    hasChildGasLeft = true;
                    childGasLeft = ParseUInt64(property.Value, $"{InputProperty}.{ChildGasLeftProperty}");
                    break;
                case ChildGasLeftProperty:
                    throw DuplicateInputProperty(property.Name);
                case ChildStateReservoirProperty when !hasChildStateReservoir:
                    hasChildStateReservoir = true;
                    childStateReservoir = ParseInt64(property.Value, $"{InputProperty}.{ChildStateReservoirProperty}");
                    break;
                case ChildStateReservoirProperty:
                    throw DuplicateInputProperty(property.Name);
                case ChildStateGasUsedProperty when !hasChildStateGasUsed:
                    hasChildStateGasUsed = true;
                    childStateGasUsed = ParseInt64(property.Value, $"{InputProperty}.{ChildStateGasUsedProperty}");
                    break;
                case ChildStateGasUsedProperty:
                    throw DuplicateInputProperty(property.Name);
                case ChildStateGasSpillProperty when !hasChildStateGasSpill:
                    hasChildStateGasSpill = true;
                    childStateGasSpill = ParseInt64(property.Value, $"{InputProperty}.{ChildStateGasSpillProperty}");
                    break;
                case ChildStateGasSpillProperty:
                    throw DuplicateInputProperty(property.Name);
                case ChildStateGasSpillRefundedProperty when !hasChildStateGasSpillRefunded:
                    hasChildStateGasSpillRefunded = true;
                    childStateGasSpillRefunded = ParseInt64(property.Value, $"{InputProperty}.{ChildStateGasSpillRefundedProperty}");
                    break;
                case ChildStateGasSpillRefundedProperty:
                    throw DuplicateInputProperty(property.Name);
                case AmountProperty when !hasAmount:
                    hasAmount = true;
                    amount = ParseInt64(property.Value, $"{InputProperty}.{AmountProperty}");
                    break;
                case AmountProperty:
                    throw DuplicateInputProperty(property.Name);
                case StateGasFloorProperty when !hasStateGasFloor:
                    hasStateGasFloor = true;
                    stateGasFloor = ParseInt64(property.Value, $"{InputProperty}.{StateGasFloorProperty}");
                    break;
                case StateGasFloorProperty:
                    throw DuplicateInputProperty(property.Name);
                case TrackSpillRefundProperty when !hasTrackSpillRefund:
                    hasTrackSpillRefund = true;
                    trackSpillRefund = ReadBoolean(property.Value, $"{InputProperty}.{TrackSpillRefundProperty}");
                    break;
                case TrackSpillRefundProperty:
                    throw DuplicateInputProperty(property.Name);
                default:
                    throw new StateGasTransitionInputException(
                        $"{InputProperty} contains unsupported property \"{property.Name}\".");
            }
        }

        if (!hasTransition)
        {
            throw new StateGasTransitionInputException($"{InputProperty} must contain {TransitionProperty}.");
        }

        if (!IsKnownTransition(transition!))
        {
            throw new StateGasTransitionInputException(
                $"{InputProperty}.{TransitionProperty} contains unsupported transition \"{transition}\".");
        }

        ValidateTransitionFields(
            transition!,
            hasGasLeft,
            hasStateReservoir,
            hasStateGasUsed,
            hasStateGasSpill,
            hasStateGasSpillRefunded,
            hasChildGasLeft,
            hasChildStateReservoir,
            hasChildStateGasUsed,
            hasChildStateGasSpill,
            hasChildStateGasSpillRefunded,
            hasAmount,
            hasStateGasFloor,
            hasTrackSpillRefund);

        return new StateGasTransitionRequest(
            transition!,
            gasLeft,
            stateReservoir,
            stateGasUsed,
            stateGasSpill,
            stateGasSpillRefunded,
            childGasLeft,
            childStateReservoir,
            childStateGasUsed,
            childStateGasSpill,
            childStateGasSpillRefunded,
            amount,
            stateGasFloor,
            trackSpillRefund);
    }

    private static void ValidateTransitionFields(
        string transition,
        bool hasGasLeft,
        bool hasStateReservoir,
        bool hasStateGasUsed,
        bool hasStateGasSpill,
        bool hasStateGasSpillRefunded,
        bool hasChildGasLeft,
        bool hasChildStateReservoir,
        bool hasChildStateGasUsed,
        bool hasChildStateGasSpill,
        bool hasChildStateGasSpillRefunded,
        bool hasAmount,
        bool hasStateGasFloor,
        bool hasTrackSpillRefund)
    {
        if (!hasGasLeft || !hasStateReservoir || !hasStateGasUsed || !hasStateGasSpill || !hasStateGasSpillRefunded)
        {
            throw new StateGasTransitionInputException(
                $"{InputProperty} must contain gasLeft, stateReservoir, stateGasUsed, stateGasSpill, and stateGasSpillRefunded.");
        }

        bool needsChildGas = transition is RefundTransition;
        bool needsChildState = transition is RefundTransition or RestoreChildStateGasTransition
            or RestoreChildStateGasOnHaltTransition or RevertRefundToHaltTransition;
        bool needsAmount = transition is RefundStateGasTransition or DiscardStateGasTransition
            or AddStateGasRefundToReservoirTransition or RemoveStateGasRefundFromReservoirTransition;
        bool needsFloor = transition is RefundStateGasTransition or DiscardStateGasTransition;
        bool needsTrack = transition is RefundStateGasTransition or AddStateGasRefundToReservoirTransition;

        RequireOrReject(hasChildGasLeft, needsChildGas, ChildGasLeftProperty, transition);
        RequireOrReject(hasChildStateReservoir, needsChildState, ChildStateReservoirProperty, transition);
        RequireOrReject(hasChildStateGasUsed, needsChildState, ChildStateGasUsedProperty, transition);
        RequireOrReject(hasChildStateGasSpill, needsChildState, ChildStateGasSpillProperty, transition);
        RequireOrReject(hasChildStateGasSpillRefunded, needsChildState, ChildStateGasSpillRefundedProperty, transition);
        RequireOrReject(hasAmount, needsAmount, AmountProperty, transition);
        RequireOrReject(hasStateGasFloor, needsFloor, StateGasFloorProperty, transition);
        RequireOrReject(hasTrackSpillRefund, needsTrack, TrackSpillRefundProperty, transition);
    }

    private static void RequireOrReject(bool present, bool required, string propertyName, string transition)
    {
        if (present != required)
        {
            string verb = required ? "must contain" : "must not contain";
            throw new StateGasTransitionInputException(
                $"{InputProperty} for {transition} {verb} {propertyName}.");
        }
    }

    private static bool IsKnownTransition(string transition) => transition is
        RefundTransition or
        RepayStateGasSpillTransition or
        RestoreChildStateGasTransition or
        RestoreChildStateGasOnHaltTransition or
        RevertRefundToHaltTransition or
        RefundStateGasTransition or
        DiscardStateGasTransition or
        AddStateGasRefundToReservoirTransition or
        RemoveStateGasRefundFromReservoirTransition;

    private static StateGasTransitionResponse ExecuteProductionAdapter(StateGasTransitionRequest request)
    {
        EthereumGasPolicy gas = new()
        {
            Value = request.GasLeft,
            StateReservoir = request.StateReservoir,
            StateGasUsed = request.StateGasUsed,
            StateGasSpill = request.StateGasSpill,
            StateGasSpillRefunded = request.StateGasSpillRefunded,
        };
        EthereumGasPolicy childGas = new()
        {
            Value = request.ChildGasLeft,
            StateReservoir = request.ChildStateReservoir,
            StateGasUsed = request.ChildStateGasUsed,
            StateGasSpill = request.ChildStateGasSpill,
            StateGasSpillRefunded = request.ChildStateGasSpillRefunded,
        };

        long unappliedAmount = 0;
        try
        {
            switch (request.Transition)
            {
                case RefundTransition:
                    EthereumGasPolicy.Refund(ref gas, in childGas);
                    break;
                case RepayStateGasSpillTransition:
                    EthereumGasPolicy.RepayStateGasSpill(ref gas);
                    break;
                case RestoreChildStateGasTransition:
                    EthereumGasPolicy.RestoreChildStateGas(ref gas, in childGas);
                    break;
                case RestoreChildStateGasOnHaltTransition:
                    EthereumGasPolicy.RestoreChildStateGasOnHalt(ref gas, in childGas);
                    break;
                case RevertRefundToHaltTransition:
                    EthereumGasPolicy.RevertRefundToHalt(ref gas, in childGas);
                    break;
                case RefundStateGasTransition:
                    EthereumGasPolicy.RefundStateGas(
                        ref gas,
                        request.Amount,
                        request.StateGasFloor,
                        request.TrackSpillRefund);
                    break;
                case DiscardStateGasTransition:
                    unappliedAmount = EthereumGasPolicy.DiscardStateGas(
                        ref gas,
                        request.Amount,
                        request.StateGasFloor);
                    break;
                case AddStateGasRefundToReservoirTransition:
                    EthereumGasPolicy.AddStateGasRefundToReservoir(
                        ref gas,
                        request.Amount,
                        request.TrackSpillRefund);
                    break;
                case RemoveStateGasRefundFromReservoirTransition:
                    EthereumGasPolicy.RemoveStateGasRefundFromReservoir(ref gas, request.Amount);
                    break;
            }
        }
        catch (ArgumentException) when (request.Transition is RemoveStateGasRefundFromReservoirTransition)
        {
            return CreateResponse(request, "exception", in gas, 0, nameof(ArgumentException));
        }

        return CreateResponse(request, "success", in gas, unappliedAmount, null);
    }

    private static StateGasTransitionResponse CreateResponse(
        StateGasTransitionRequest request,
        string outcome,
        in EthereumGasPolicy gas,
        long unappliedAmount,
        string? error) =>
        new(
            CurrentSchemaVersion,
            Operation,
            request.Transition,
            outcome,
            new StateGasTransitionState(
                gas.Value.ToString(CultureInfo.InvariantCulture),
                gas.StateReservoir.ToString(CultureInfo.InvariantCulture),
                gas.StateGasUsed.ToString(CultureInfo.InvariantCulture),
                gas.StateGasSpill.ToString(CultureInfo.InvariantCulture),
                gas.StateGasSpillRefunded.ToString(CultureInfo.InvariantCulture),
                unappliedAmount.ToString(CultureInfo.InvariantCulture)),
            error);

    private static void ValidateEnvelopeProperties(JsonElement root)
    {
        bool hasSchemaVersion = false;
        bool hasOperation = false;
        bool hasInput = false;

        foreach (JsonProperty property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case SchemaVersionProperty when !hasSchemaVersion:
                    hasSchemaVersion = true;
                    break;
                case OperationProperty when !hasOperation:
                    hasOperation = true;
                    break;
                case InputProperty when !hasInput:
                    hasInput = true;
                    break;
                case SchemaVersionProperty:
                case OperationProperty:
                case InputProperty:
                    throw new StateGasTransitionInputException($"root contains duplicate property \"{property.Name}\".");
                default:
                    throw new StateGasTransitionInputException($"root contains unsupported property \"{property.Name}\".");
            }
        }

        if (!hasSchemaVersion || !hasOperation || !hasInput)
        {
            throw new StateGasTransitionInputException("root must contain schemaVersion, operation, and input.");
        }
    }

    private static string ReadRequiredString(JsonElement objectElement, string propertyName, string location)
    {
        JsonElement value = objectElement.GetProperty(propertyName);
        return ReadString(value, $"{location}.{propertyName}");
    }

    private static string ReadString(JsonElement value, string location)
    {
        if (value.ValueKind is not JsonValueKind.String)
        {
            throw new StateGasTransitionInputException($"{location} must be a JSON string.");
        }

        return value.GetString()!;
    }

    private static bool ReadBoolean(JsonElement value, string location)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new StateGasTransitionInputException($"{location} must be a JSON boolean.");
        }

        return value.GetBoolean();
    }

    private static JsonElement ReadRequiredObject(JsonElement objectElement, string propertyName, string location)
    {
        JsonElement value = objectElement.GetProperty(propertyName);
        if (value.ValueKind is not JsonValueKind.Object)
        {
            throw new StateGasTransitionInputException($"{location}.{propertyName} must be an object.");
        }

        return value;
    }

    private static ulong ParseUInt64(JsonElement value, string location) =>
        ParseUInt64(ReadString(value, location), location);

    private static ulong ParseUInt64(string value, string location)
    {
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed))
        {
            throw new StateGasTransitionInputException($"{location} must be a decimal string in the UInt64 range.");
        }

        return parsed;
    }

    private static long ParseInt64(JsonElement value, string location) =>
        ParseInt64(ReadString(value, location), location);

    private static long ParseInt64(string value, string location)
    {
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed))
        {
            throw new StateGasTransitionInputException($"{location} must be a decimal string in the Int64 range.");
        }

        return parsed;
    }

    private static StateGasTransitionInputException DuplicateInputProperty(string propertyName) =>
        new($"{InputProperty} contains duplicate property \"{propertyName}\".");

    private static int Fail(TextWriter error, int lineNumber, string message)
    {
        error.WriteLine($"formal state-transition: line {lineNumber}: {message}");
        return InvalidInputExitCode;
    }

    private readonly record struct StateGasTransitionRequest(
        string Transition,
        ulong GasLeft,
        long StateReservoir,
        long StateGasUsed,
        long StateGasSpill,
        long StateGasSpillRefunded,
        ulong ChildGasLeft,
        long ChildStateReservoir,
        long ChildStateGasUsed,
        long ChildStateGasSpill,
        long ChildStateGasSpillRefunded,
        long Amount,
        long StateGasFloor,
        bool TrackSpillRefund);

    private sealed record StateGasTransitionResponse(
        string SchemaVersion,
        string Operation,
        string Transition,
        string Outcome,
        StateGasTransitionState State,
        string? Error);

    private sealed record StateGasTransitionState(
        string GasLeft,
        string StateReservoir,
        string StateGasUsed,
        string StateGasSpill,
        string StateGasSpillRefunded,
        string UnappliedAmount);

    private sealed class StateGasTransitionInputException(string message) : Exception(message);
}
