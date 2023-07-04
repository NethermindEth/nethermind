// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public interface IDiscoveryApp : INodeSource
    {
        void Initialize(PublicKey masterPublicKey);
        void Start();
        Task StopAsync();
        void AddNodeToDiscovery(Node node);
    }
}
