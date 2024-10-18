// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Core;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nethermind.Taiko.Test")]

namespace Nethermind.Taiko;
internal static class TaikoAddressHelper
{
    private const string TaikoL2AddressSuffix = "10001";

    public static Address GetTaikoL2ContractAddress(ISpecProvider specProvider) => new(
        specProvider.ChainId.ToString().PadRight(40 - TaikoL2AddressSuffix.Length, '0') + TaikoL2AddressSuffix
        );
}
