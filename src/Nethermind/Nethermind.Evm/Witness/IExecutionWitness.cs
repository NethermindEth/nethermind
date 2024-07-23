// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Witness;

public interface IExecutionWitness
{
    long AccessForContractCreationInit(Address contractAddress, bool isValueTransfer);
    long AccessForContractCreated(Address contractAddress);

    /// <summary>
    ///     When you are starting to execute a transaction.
    /// </summary>
    /// <param name="originAddress"></param>
    /// <param name="destinationAddress"></param>
    /// <param name="isValueTransfer"></param>
    /// <returns></returns>
    long AccessForTransaction(Address originAddress, Address? destinationAddress, bool isValueTransfer);

    /// <summary>
    ///     Call for the gas beneficiary.
    /// </summary>
    /// <param name="gasBeneficiary"></param>
    /// <returns></returns>
    long AccessForGasBeneficiary(Address gasBeneficiary);

    long AccessForCodeOpCodes(Address caller);
    long AccessForBalance(Address address, bool isWrite = false);
    long AccessForCodeHash(Address address);

    /// <summary>
    ///     When SLOAD and SSTORE opcodes are called with a given address
    ///     and key.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="key"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    long AccessForStorage(Address address, UInt256 key, bool isWrite);

    long AccessForCodeProgramCounter(Address address, int programCounter, bool isWrite);
    bool AccessAndChargeForCodeSlice(Address address, int startIncluded, int endNotIncluded, bool isWrite, ref long unspentGas);

    /// <summary>
    ///     When the code chunk chunk_id is accessed is accessed
    /// </summary>
    /// <param name="address"></param>
    /// <param name="chunkId"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    long AccessCodeChunk(Address address, UInt256 chunkId, bool isWrite);

    long AccessForAbsentAccount(Address address);

    /// <summary>
    ///     When you have to access the complete account
    /// </summary>
    /// <param name="address"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    long AccessCompleteAccount(Address address, bool isWrite = false);

    long AccessForSelfDestruct(Address contract, Address inheritor, bool balanceIsZero, bool inheritorExist);
    byte[][] GetAccessedKeys();
}
