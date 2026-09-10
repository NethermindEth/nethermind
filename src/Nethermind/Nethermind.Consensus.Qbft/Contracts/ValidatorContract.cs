// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.Contracts;

/// <summary>Read side of the QBFT validator smart contract: <c>function getValidators() view returns (address[])</c>.</summary>
public interface IValidatorContract
{
    /// <summary>The validator set the contract at <paramref name="contractAddress"/> reports in the state after <paramref name="header"/>.</summary>
    /// <exception cref="AbiException">The contract is missing or the call reverted.</exception>
    Address[] GetValidators(BlockHeader header, Address contractAddress);
}

/// <summary>
/// Calls the validator contract with a system transaction; the contract address is per call because
/// QBFT transitions may move it.
/// </summary>
/// <remarks>Mirrors Besu's <c>ValidatorContractController</c>, which evaluates the call at the block's own state.</remarks>
public sealed class ValidatorContract : Contract, IValidatorContract
{
    private readonly IConstantContract _constant;

    public ValidatorContract(IAbiEncoder abiEncoder, IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        : base(abiEncoder) => _constant = GetConstant(readOnlyTxProcessorSource);

    public Address[] GetValidators(BlockHeader header, Address contractAddress) =>
        _constant.Call<Address[]>(header, contractAddress, nameof(GetValidators), Address.Zero);
}
