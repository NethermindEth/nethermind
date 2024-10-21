// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.ChainSpecStyle
{
    public static class ValidatorTypeExtensions
    {
        public static bool CanChangeImmediately(this ValidatorType validatorType) =>
            validatorType switch
            {
                ValidatorType.Contract => false,
                ValidatorType.ReportingContract => false,
                ValidatorType.List => true,
                ValidatorType.Multi => true,
                _ => false
            };
    }
}
