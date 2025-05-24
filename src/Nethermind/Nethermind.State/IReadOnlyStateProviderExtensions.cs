// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using System;

namespace Nethermind.State
{
    public static class IReadOnlyStateProviderExtensions
    {
        public static byte[] GetCode(this IReadOnlyStateProvider stateProvider, Address address)
        {
            stateProvider.TryGetAccount(address, out AccountStruct account);
            return !account.HasCode ? [] : stateProvider.GetCode(in account.CodeHash) ?? [];
        }
        /// <summary>
        /// Checks if <paramref name="sender"/> has code that is not a delegation, according to the rules of eip-3607 and eip-7702.
        /// Where possible a cache for code lookup should be used, since the fallback will read from <see cref="GetCode(IReadOnlyStateProvider, Address)"/>.
        /// </summary>
        /// <param name="stateProvider"></param>
        /// <param name="spec"></param>
        /// <param name="sender"></param>
        /// <param name="isDelegatedCode"></param>
        /// <returns></returns>
        public static bool IsInvalidContractSender(
            this IReadOnlyStateProvider stateProvider,
            IReleaseSpec spec,
            Address sender,
            Func<Address, bool>? isDelegatedCode = null) =>
            spec.IsEip3607Enabled
            && stateProvider.HasCode(sender)
            && (!spec.IsEip7702Enabled
                || (!isDelegatedCode?.Invoke(sender) ?? !Eip7702Constants.IsDelegatedCode(GetCode(stateProvider, sender))));
    }

}
