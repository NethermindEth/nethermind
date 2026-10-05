// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Nethermind.EthStats.Messages;
using Nethermind.Logging;
using Websocket.Client;

namespace Nethermind.EthStats.Senders
{
    public partial class MessageSender(string instanceId, ILogManager logManager) : IMessageSender
    {
        private readonly string _instanceId = instanceId;
        private readonly ILogger _logger = logManager.GetClassLogger<MessageSender>();

        public Task SendAsync<T>(IWebsocketClient? client, T message, string? type = null) where T : IMessage
        {
            if (client is null)
            {
                return Task.CompletedTask;
            }

            (EmitMessage? emitMessage, string? messageType) = CreateMessage(message, type);
            string payload = JsonSerializer.Serialize(emitMessage, MessageJsonContext.Default.EmitMessage);
            if (_logger.IsTrace) _logger.Trace($"Sending ETH stats message '{messageType}': {payload}");

            client.Send(payload);
            return Task.CompletedTask;
        }

        private (EmitMessage message, string type) CreateMessage<T>(T message, string? type = null) where T : IMessage
        {
            message.Id = _instanceId;
            string messageType = string.IsNullOrWhiteSpace(type)
                ? typeof(T).Name.ToLowerInvariant().Replace("message", string.Empty)
                : type;

            return (new EmitMessage(messageType, message), messageType);
        }

        private class EmitMessage
        {
            // ReSharper disable once CollectionNeverQueried.Local
            // ReSharper disable once MemberCanBePrivate.Local
            public List<object> Emit { get; } = [];

            public EmitMessage(string type, object message)
            {
                Emit.Add(type);
                Emit.Add(message);
            }
        }

        [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
        [JsonSerializable(typeof(EmitMessage))]
        [JsonSerializable(typeof(string))]
        [JsonSerializable(typeof(BlockMessage))]
        [JsonSerializable(typeof(HelloMessage))]
        [JsonSerializable(typeof(HistoryMessage))]
        [JsonSerializable(typeof(LatencyMessage))]
        [JsonSerializable(typeof(NodePingMessage))]
        [JsonSerializable(typeof(NodePongMessage))]
        [JsonSerializable(typeof(PendingMessage))]
        [JsonSerializable(typeof(PingMessage))]
        [JsonSerializable(typeof(StatsMessage))]
        private partial class MessageJsonContext : JsonSerializerContext;
    }
}
