// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Validators;
public interface IBuilderSubmissionValidator
{
    void ValidateBuilderSubmission(Block builderBlock, BidTrace message, uint registeredGasLimit, Keccak? withdrawalsRoot = null);
}
