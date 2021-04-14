//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Websocket.Client;

namespace Nethermind.EthStats.Senders
{
    public class MessageSender : IMessageSender
    {
        private static readonly JsonSerializerSettings SerializerSettings = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

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
            string payload = JsonConvert.SerializeObject(emitMessage, SerializerSettings);
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
