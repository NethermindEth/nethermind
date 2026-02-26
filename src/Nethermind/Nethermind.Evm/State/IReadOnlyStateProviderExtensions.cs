// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using System;

namespace Nethermind.Evm.State
{
    public static class IReadOnlyStateProviderExtensions
    {
        public static bool IsInvalidContractSender(
            this IReadOnlyStateProvider stateProvider,
            in SpecSnapshot spec,
            Address sender,
            Func<Address, bool>? isDelegatedCode = null) =>
            spec.IsEip3607Enabled
            && stateProvider.HasCode(sender)
            && (!spec.IsEip7702Enabled
                || (!isDelegatedCode?.Invoke(sender) ?? !Eip7702Constants.IsDelegatedCode(stateProvider.GetCode(sender))));

        public static bool IsInvalidContractSender(
            this IReadOnlyStateProvider stateProvider,
            IReleaseSpec spec,
            Address sender,
            Func<Address, bool>? isDelegatedCode = null) =>
            spec.IsEip3607Enabled
            && stateProvider.HasCode(sender)
            && (!spec.IsEip7702Enabled
                || (!isDelegatedCode?.Invoke(sender) ?? !Eip7702Constants.IsDelegatedCode(stateProvider.GetCode(sender))));

        public static bool IsInvalidContractSender(
            this IWorldState stateProvider,
            in SpecSnapshot spec,
            Address sender,
            Func<Address, bool>? isDelegatedCode = null) =>
            spec.IsEip3607Enabled
            && stateProvider.IsContract(sender)
            && (!spec.IsEip7702Enabled
                || (!isDelegatedCode?.Invoke(sender) ?? !Eip7702Constants.IsDelegatedCode(stateProvider.GetCode(sender))));

        public static bool IsInvalidContractSender(
            this IWorldState stateProvider,
            IReleaseSpec spec,
            Address sender,
            Func<Address, bool>? isDelegatedCode = null) =>
            spec.IsEip3607Enabled
            && stateProvider.IsContract(sender)
            && (!spec.IsEip7702Enabled
                || (!isDelegatedCode?.Invoke(sender) ?? !Eip7702Constants.IsDelegatedCode(stateProvider.GetCode(sender))));
    }
}
