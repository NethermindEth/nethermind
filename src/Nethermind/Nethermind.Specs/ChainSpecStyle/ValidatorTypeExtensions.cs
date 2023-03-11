// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.ChainSpecStyle
{
    public static class ValidatorTypeExtensions
    {
        public static bool CanChangeImmediately(this AuRaParameters.ValidatorType validatorType) =>
            validatorType switch
            {
                AuRaParameters.ValidatorType.Contract => false,
                AuRaParameters.ValidatorType.ReportingContract => false,
                AuRaParameters.ValidatorType.List => true,
                AuRaParameters.ValidatorType.Multi => true,
                _ => false
            };
    }
}
