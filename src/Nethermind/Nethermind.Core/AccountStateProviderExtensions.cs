// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public static class AccountStateProviderExtensions
    {
        public static bool HasCode(this IAccountStateProvider stateProvider, Address address) =>
            stateProvider.TryGetAccount(address, out AccountStruct account) && account.HasCode;

        public static bool IsInvalidContractSender(this IAccountStateProvider stateProvider, IReleaseSpec spec, Address address) =>
            spec.IsEip3607Enabled && stateProvider.HasCode(address);
    }
}
