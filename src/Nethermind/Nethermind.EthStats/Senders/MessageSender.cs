// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Websocket.Client;

namespace Nethermind.EthStats.Senders
{
    public class MessageSender : IMessageSender
    {
        private readonly string _instanceId;
        private readonly ILogger _logger;

        public MessageSender(string instanceId, ILogManager logManager)
        {
            _instanceId = instanceId;
            _logger = logManager.GetClassLogger();
        }

        public Task SendAsync<T>(IWebsocketClient? client, T message, string? type = null) where T : IMessage
        {
            if (client is null)
            {
                return Task.CompletedTask;
            }

            (EmitMessage? emitMessage, string? messageType) = CreateMessage(message, type);
            string payload = JsonSerializer.Serialize(emitMessage, EthereumJsonSerializer.JsonOptions);
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
            public List<object> Emit { get; } = new();

            public EmitMessage(string type, object message)
            {
                Emit.Add(type);
                Emit.Add(message);
            }
        }
    }
}
