// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Core.ServiceStopper;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public interface IDiscoveryApp : INodeSource, IStoppableService
    {
        void InitializeChannel(IChannel channel);
        Task StartAsync();
        void AddNodeToDiscovery(Node node);
    }
}
