// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Channels
{
    public interface INdmConsumerChannelManager
    {
        void Add(INdmConsumerChannel ndmConsumerChannel);
        void Remove(INdmConsumerChannel ndmConsumerChannel);
        Task PublishAsync(Keccak depositId, string client, string data);
    }
}
