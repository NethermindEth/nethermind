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
            return !account.HasCode ? Array.Empty<byte>() : stateProvider.GetCode(account.CodeHash) ?? Array.Empty<byte>();
        }
        public static bool IsInvalidContractSender(this IReadOnlyStateProvider stateProvider, IReleaseSpec spec, Address address) =>
           spec.IsEip3607Enabled && stateProvider.HasCode(address) && !Eip7702Constants.IsDelegatedCode(GetCode(stateProvider, address));
    }
}
