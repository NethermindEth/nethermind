// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.DataRequests
{
    public interface IDataRequestFactory
    {
        DataRequest Create(Deposit deposit, Keccak dataAssetId, Address provider, Address consumer, byte[] pepper);
    }
}
