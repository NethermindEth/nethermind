// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Providers.Policies
{
    public interface IRefundPolicy
    {
        uint GetClaimableAfterUnits(Keccak depositId);
    }
}
