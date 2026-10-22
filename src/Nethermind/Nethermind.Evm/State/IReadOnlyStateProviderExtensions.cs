// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using System;

namespace Nethermind.Evm.State
{
    public static class IReadOnlyStateProviderExtensions
    {
        /// <summary>The code of <paramref name="address"/> as a span; empty for an account without code.</summary>
        public static ReadOnlySpan<byte> GetCodeSpan(this IReadOnlyStateProvider stateProvider, Address address) =>
            stateProvider.GetCode(address).Span;

        /// <summary>The code stored under <paramref name="codeHash"/> as a span; null when the provider cannot serve it.</summary>
        public static ReadOnlySpan<byte> GetCodeSpan(this IReadOnlyStateProvider stateProvider, in ValueHash256 codeHash) =>
            stateProvider.GetCode(in codeHash).Span;

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
                || (!isDelegatedCode?.Invoke(sender) ?? !Eip7702Constants.IsDelegatedCode(stateProvider.GetCodeSpan(sender))));

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
            this IWorldState stateProvider,
            IReleaseSpec spec,
            Address sender,
            Func<Address, bool>? isDelegatedCode = null) => spec.IsEip3607Enabled
            && stateProvider.IsContract(sender)
            && (!spec.IsEip7702Enabled
                || (!isDelegatedCode?.Invoke(sender) ?? !Eip7702Constants.IsDelegatedCode(stateProvider.GetCodeSpan(sender))));
    }

}
