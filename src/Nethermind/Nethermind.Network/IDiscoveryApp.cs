// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

using DotNetty.Transport.Channels;

using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public interface IDiscoveryApp : INodeSource
    {
        void InitializeChannel(IChannel channel);
        Task StartAsync();
        Task StopAsync();
        void AddNodeToDiscovery(Node node);
    }
}
