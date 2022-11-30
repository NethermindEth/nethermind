// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Sockets;

namespace Nethermind.DataMarketplace.Infrastructure.Notifiers
{
    public class NdmNotifier : INdmNotifier
    {
        private readonly IWebSocketsModule _webSocketsModule;

        public NdmNotifier(IWebSocketsModule webSocketsModule)
        {
            _webSocketsModule = webSocketsModule;
        }

        public Task NotifyAsync(Notification notification)
            => _webSocketsModule.SendAsync(new SocketsMessage(notification.Type, notification.Client,
                notification.Data));
    }
}
