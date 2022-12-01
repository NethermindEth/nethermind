// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Consumers.Deposits
{
    public interface IKycVerifier
    {
        Task<bool> IsVerifiedAsync(Keccak dataAssetId, Address address);
    }
}
