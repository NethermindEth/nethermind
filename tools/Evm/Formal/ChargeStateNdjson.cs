// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.IO;
using System.Text.Json;
using Nethermind.Evm.GasPolicy;

namespace Evm.Formal;

internal static class ChargeStateNdjson
{
    private const string CurrentSchemaVersion = "1";
    private const string Operation = "charge-state";
    private const int InvalidInputExitCode = 2;
    private const string SchemaVersionProperty = "schemaVersion";
    private const string OperationProperty = "operation";
    private const string InputProperty = "input";
    private const string GasLeftProperty = "gasLeft";
    private const string StateReservoirProperty = "stateReservoir";
    private const string StateGasUsedProperty = "stateGasUsed";
    private const string StateGasSpillProperty = "stateGasSpill";
    private const string StateGasSpillRefundedProperty = "stateGasSpillRefunded";
    private const string StateGasCostProperty = "stateGasCost";

    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
                ChargeStateRequest request = ParseRequest(line);
                ChargeStateResponse response = ExecuteProductionAdapter(request);
                output.WriteLine(JsonSerializer.Serialize(response, OutputJsonOptions));
            }
            catch (JsonException exception)
            {
                return Fail(error, lineNumber, $"invalid JSON: {exception.Message}");
            }
            catch (ChargeStateInputException exception)
            {
                return Fail(error, lineNumber, exception.Message);
            }
        }

        return 0;
    }

    private static ChargeStateRequest ParseRequest(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new ChargeStateInputException("the JSON root must be an object.");
        }

        ValidateEnvelopeProperties(root);
        string schemaVersion = ReadRequiredString(root, SchemaVersionProperty, "root");
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ChargeStateInputException($"{SchemaVersionProperty} must be \"{CurrentSchemaVersion}\".");
        }

        string operation = ReadRequiredString(root, OperationProperty, "root");
        if (operation != Operation)
        {
            throw new ChargeStateInputException($"{OperationProperty} must be \"{Operation}\".");
        }

        JsonElement input = ReadRequiredObject(root, InputProperty, "root");
        ValidateInputProperties(input);

        return new ChargeStateRequest(
            ParseUInt64(ReadRequiredString(input, GasLeftProperty, InputProperty), $"{InputProperty}.{GasLeftProperty}"),
            ParseInt64(ReadRequiredString(input, StateReservoirProperty, InputProperty), $"{InputProperty}.{StateReservoirProperty}"),
            ParseInt64(ReadRequiredString(input, StateGasUsedProperty, InputProperty), $"{InputProperty}.{StateGasUsedProperty}"),
            ParseInt64(ReadRequiredString(input, StateGasSpillProperty, InputProperty), $"{InputProperty}.{StateGasSpillProperty}"),
            ParseInt64(ReadRequiredString(input, StateGasSpillRefundedProperty, InputProperty), $"{InputProperty}.{StateGasSpillRefundedProperty}"),
            ParseInt64(ReadRequiredString(input, StateGasCostProperty, InputProperty), $"{InputProperty}.{StateGasCostProperty}"));
    }

    private static ChargeStateResponse ExecuteProductionAdapter(ChargeStateRequest request)
    {
        EthereumGasPolicy gas = new()
        {
            Value = request.GasLeft,
            StateReservoir = request.StateReservoir,
            StateGasUsed = request.StateGasUsed,
            StateGasSpill = request.StateGasSpill,
            StateGasSpillRefunded = request.StateGasSpillRefunded,
        };

        bool success = EthereumGasPolicy.TryConsumeStateGas(ref gas, request.StateGasCost);

        return new ChargeStateResponse(
            CurrentSchemaVersion,
            Operation,
            success ? "success" : "outOfGas",
            new ChargeStateState(
                gas.Value.ToString(CultureInfo.InvariantCulture),
                gas.StateReservoir.ToString(CultureInfo.InvariantCulture),
                gas.StateGasUsed.ToString(CultureInfo.InvariantCulture),
                gas.StateGasSpill.ToString(CultureInfo.InvariantCulture),
                gas.StateGasSpillRefunded.ToString(CultureInfo.InvariantCulture)));
    }

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
                    throw new ChargeStateInputException($"root contains duplicate property \"{property.Name}\".");
                default:
                    throw new ChargeStateInputException($"root contains unsupported property \"{property.Name}\".");
            }
        }

        if (!hasSchemaVersion || !hasOperation || !hasInput)
        {
            throw new ChargeStateInputException("root must contain schemaVersion, operation, and input.");
        }
    }

    private static void ValidateInputProperties(JsonElement input)
    {
        bool hasGasLeft = false;
        bool hasStateReservoir = false;
        bool hasStateGasUsed = false;
        bool hasStateGasSpill = false;
        bool hasStateGasSpillRefunded = false;
        bool hasStateGasCost = false;

        foreach (JsonProperty property in input.EnumerateObject())
        {
            switch (property.Name)
            {
                case GasLeftProperty when !hasGasLeft:
                    hasGasLeft = true;
                    break;
                case StateReservoirProperty when !hasStateReservoir:
                    hasStateReservoir = true;
                    break;
                case StateGasUsedProperty when !hasStateGasUsed:
                    hasStateGasUsed = true;
                    break;
                case StateGasSpillProperty when !hasStateGasSpill:
                    hasStateGasSpill = true;
                    break;
                case StateGasSpillRefundedProperty when !hasStateGasSpillRefunded:
                    hasStateGasSpillRefunded = true;
                    break;
                case StateGasCostProperty when !hasStateGasCost:
                    hasStateGasCost = true;
                    break;
                case GasLeftProperty:
                case StateReservoirProperty:
                case StateGasUsedProperty:
                case StateGasSpillProperty:
                case StateGasSpillRefundedProperty:
                case StateGasCostProperty:
                    throw new ChargeStateInputException($"{InputProperty} contains duplicate property \"{property.Name}\".");
                default:
                    throw new ChargeStateInputException($"{InputProperty} contains unsupported property \"{property.Name}\".");
            }
        }

        if (!hasGasLeft || !hasStateReservoir || !hasStateGasUsed || !hasStateGasSpill || !hasStateGasSpillRefunded || !hasStateGasCost)
        {
            throw new ChargeStateInputException(
                "input must contain gasLeft, stateReservoir, stateGasUsed, stateGasSpill, stateGasSpillRefunded, and stateGasCost.");
        }
    }

    private static string ReadRequiredString(JsonElement objectElement, string propertyName, string location)
    {
        JsonElement value = objectElement.GetProperty(propertyName);
        if (value.ValueKind is not JsonValueKind.String)
        {
            throw new ChargeStateInputException($"{location}.{propertyName} must be a JSON string.");
        }

        return value.GetString()!;
    }

    private static JsonElement ReadRequiredObject(JsonElement objectElement, string propertyName, string location)
    {
        JsonElement value = objectElement.GetProperty(propertyName);
        if (value.ValueKind is not JsonValueKind.Object)
        {
            throw new ChargeStateInputException($"{location}.{propertyName} must be an object.");
        }

        return value;
    }

    private static ulong ParseUInt64(string value, string location)
    {
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed))
        {
            throw new ChargeStateInputException($"{location} must be a decimal string in the UInt64 range.");
        }

        return parsed;
    }

    private static long ParseInt64(string value, string location)
    {
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed))
        {
            throw new ChargeStateInputException($"{location} must be a decimal string in the Int64 range.");
        }

        return parsed;
    }

    private static int Fail(TextWriter error, int lineNumber, string message)
    {
        error.WriteLine($"formal charge-state: line {lineNumber}: {message}");
        return InvalidInputExitCode;
    }

    private readonly record struct ChargeStateRequest(
        ulong GasLeft,
        long StateReservoir,
        long StateGasUsed,
        long StateGasSpill,
        long StateGasSpillRefunded,
        long StateGasCost);

    private sealed record ChargeStateResponse(
        string SchemaVersion,
        string Operation,
        string Outcome,
        ChargeStateState State);

    private sealed record ChargeStateState(
        string GasLeft,
        string StateReservoir,
        string StateGasUsed,
        string StateGasSpill,
        string StateGasSpillRefunded);

    private sealed class ChargeStateInputException(string message) : Exception(message);
}
