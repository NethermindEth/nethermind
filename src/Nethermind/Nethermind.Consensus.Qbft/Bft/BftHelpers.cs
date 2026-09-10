// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft.Bft;

public static class BftHelpers
{
    /// <summary>The constant <c>mixHash</c> every BFT block carries: ASCII "ctical byzantine fault tolerance".</summary>
    public static readonly Hash256 ExpectedMixHash = new("0x63746963616c2062797a616e74696e65206661756c7420746f6c6572616e6365");

    /// <summary>Messages needed to reach agreement: <c>ceil(2n / 3)</c>.</summary>
    public static int CalculateRequiredValidatorQuorum(int validatorCount) => (2 * validatorCount + 2) / 3;

    /// <summary>Round-change messages from distinct validators that prove at least one honest node moved on: <c>f + 1</c>.</summary>
    public static int CalculateRequiredFutureRoundChangeQuorum(int validatorCount) => (validatorCount - 1) / 3 + 1;
}
