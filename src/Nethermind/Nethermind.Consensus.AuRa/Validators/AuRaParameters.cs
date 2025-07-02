// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Validators;

public class AuRaParameters
{
    public enum ValidatorType
    {
        List,
        Contract,
        ReportingContract,
        Multi
    }

    public class Validator
    {
        public ValidatorType ValidatorType { get; set; }

        /// <summary>
        /// Dictionary of Validators per their starting block.
        /// </summary>
        /// <remarks>
        /// Only Valid for <seealso cref="ValidatorType"/> of type <see cref="AuRaParameters.AuRaParameters.ValidatorType.Multi"/>.
        ///
        /// This has to sorted in order of starting blocks.
        /// </remarks>
        public IDictionary<long, Validator>? Validators { get; set; }

        /// <summary>
        /// Addresses for validator.
        /// </summary>
        /// <remarks>
        /// For <seealso cref="ValidatorType"/> of type <see cref="AuRaParameters.AuRaParameters.ValidatorType.List"/> should contain at least one address.
        /// For <seealso cref="ValidatorType"/> of type <see cref="AuRaParameters.AuRaParameters.ValidatorType.Contract"/> and <see cref="AuRaParameters.AuRaParameters.ValidatorType.ReportingContract"/> should contain exactly one address.
        /// For <seealso cref="ValidatorType"/> of type <see cref="AuRaParameters.AuRaParameters.ValidatorType.Multi"/> will be empty.
        /// </remarks>
        public Address[]? Addresses { get; set; }

        public Address GetContractAddress()
        {
            return ValidatorType switch
            {
                ValidatorType.Contract or ValidatorType.ReportingContract => Addresses?.FirstOrDefault() ?? throw new ArgumentException("Missing contract address for AuRa validator.", nameof(Addresses)),
                _ => throw new InvalidOperationException($"AuRa validator {ValidatorType} doesn't have contract address."),
            };
        }
    }
}

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
