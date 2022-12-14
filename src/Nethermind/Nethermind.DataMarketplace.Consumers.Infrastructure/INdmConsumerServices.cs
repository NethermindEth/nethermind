// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Consumers.Shared;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure
{
    public interface INdmConsumerServices
    {
        IAccountService AccountService { get; }
        IConsumerService ConsumerService { get; }
    }
}
