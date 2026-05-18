// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

namespace Nethermind.EthStats;

internal enum EthStatsIncomingMessageType
{
    Unknown,
    History,
    NodePing,
    NodePong
}

internal readonly record struct EthStatsHistoryRequest(long Min, long Max);

internal readonly record struct EthStatsNodeTiming(long? ClientTime, long? ServerTime);

internal readonly record struct EthStatsIncomingMessage(
    string EventTypeName,
    EthStatsIncomingMessageType MessageType,
    EthStatsHistoryRequest? HistoryRequest,
    EthStatsNodeTiming? NodeTiming);

internal static class EthStatsMessageParser
{
    public static bool TryParse(string? message, out EthStatsIncomingMessage incomingMessage)
    {
        incomingMessage = new EthStatsIncomingMessage(string.Empty, EthStatsIncomingMessageType.Unknown, null, null);

        if (string.IsNullOrWhiteSpace(message) || message[0] != '{')
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(message);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("emit", out JsonElement emit) ||
                emit.ValueKind != JsonValueKind.Array ||
                emit.GetArrayLength() == 0)
            {
                return false;
            }

            JsonElement eventTypeElement = emit[0];
            if (eventTypeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? eventType = eventTypeElement.GetString();
            if (string.IsNullOrWhiteSpace(eventType))
            {
                return false;
            }

            JsonElement payload = emit.GetArrayLength() > 1 ? emit[1] : default;
            if (eventType == "history")
            {
                if (!TryReadHistoryRequest(payload, out EthStatsHistoryRequest historyRequest))
                {
                    return false;
                }

                incomingMessage = new EthStatsIncomingMessage(eventType, EthStatsIncomingMessageType.History, historyRequest, null);
                return true;
            }

            incomingMessage = eventType switch
            {
                "node-ping" =>
                    new EthStatsIncomingMessage(eventType, EthStatsIncomingMessageType.NodePing, null, ReadNodeTiming(payload)),
                "node-pong" =>
                    new EthStatsIncomingMessage(eventType, EthStatsIncomingMessageType.NodePong, null, ReadNodeTiming(payload)),
                _ =>
                    new EthStatsIncomingMessage(eventType, EthStatsIncomingMessageType.Unknown, null, null)
            };

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadHistoryRequest(JsonElement payload, out EthStatsHistoryRequest historyRequest)
    {
        historyRequest = default;

        if (!TryGetInt64(payload, "min", out long min) ||
            !TryGetInt64(payload, "max", out long max))
        {
            return false;
        }

        historyRequest = new EthStatsHistoryRequest(min, max);
        return true;
    }

    private static EthStatsNodeTiming ReadNodeTiming(JsonElement payload)
    {
        long? clientTime = TryGetInt64(payload, "clientTime", out long parsedClientTime) ? parsedClientTime : null;
        long? serverTime = TryGetInt64(payload, "serverTime", out long parsedServerTime) ? parsedServerTime : null;

        return new EthStatsNodeTiming(clientTime, serverTime);
    }

    private static bool TryGetInt64(JsonElement payload, string propertyName, out long value)
    {
        value = 0;

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in payload.EnumerateObject())
        {
            if (property.NameEquals(propertyName))
            {
                return TryReadInt64(property.Value, out value);
            }
        }

        return false;
    }

    private static bool TryReadInt64(JsonElement element, out long value)
    {
        value = 0;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(element.GetString(), out value),
            _ => false
        };
    }
}
