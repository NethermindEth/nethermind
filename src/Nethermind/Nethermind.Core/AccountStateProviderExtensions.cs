// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core
{
    [SkipLocalsInit]
    public static class AccountStateProviderExtensions
    {
        public static bool HasCode(this IAccountStateProvider stateProvider, Address address, int? blockAccessIndex) =>
            stateProvider.TryGetAccount(address, out AccountStruct account, blockAccessIndex) && account.HasCode;
    }
}
