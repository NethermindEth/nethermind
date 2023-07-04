// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus
{
    public interface ISealValidator
    {
        bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false);

        /// <summary>
        /// Validates block header seal.
        /// </summary>
        /// <param name="header">Block header to validate.</param>
        /// <param name="force">Unless set to <value>true</value> the validator is allowed to optimize validation away in a safe manner.</param>
        /// <returns><value>True</value> if seal is valid or was not checked, otherwise <value>false</value></returns>
        bool ValidateSeal(BlockHeader header, bool force);

        public void HintValidationRange(Guid guid, long start, long end) { }
    }
}
