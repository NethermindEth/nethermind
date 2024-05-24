// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Witness;

public interface IExecutionWitness
{
    bool AccessForContractCreationInit(Address contractAddress, ref long gasAvailable, bool isValueTransfer);
    bool AccessForContractCreated(Address contractAddress, ref long gasAvailable);

    /// <summary>
    ///     When you are starting to execute a transaction.
    /// </summary>
    /// <param name="originAddress"></param>
    /// <param name="destinationAddress"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isValueTransfer"></param>
    /// <returns></returns>
    bool AccessForTransaction(Address originAddress, Address? destinationAddress, ref long gasAvailable, bool isValueTransfer);

    /// <summary>
    ///     Call for the gas beneficiary.
    /// </summary>
    /// <param name="gasBeneficiary"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    bool AccessForGasBeneficiary(Address gasBeneficiary, ref long gasAvailable);

    bool AccessForCodeOpCodes(Address caller, ref long gasAvailable);
    bool AccessForBalance(Address address, ref long gasAvailable, bool isWrite = false);
    bool AccessForCodeHash(Address address, ref long gasAvailable);

    /// <summary>
    ///     When SLOAD and SSTORE opcodes are called with a given address
    ///     and key.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="key"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    bool AccessForStorage(Address address, UInt256 key, ref long gasAvailable, bool isWrite);

    bool AccessForCodeProgramCounter(Address address, int programCounter, ref long gasAvailable, bool isWrite);
    bool AccessAndChargeForCodeSlice(Address address, int startIncluded, int endNotIncluded, ref long gasAvailable, bool isWrite);

    /// <summary>
    ///     When the code chunk chunk_id is accessed is accessed
    /// </summary>
    /// <param name="address"></param>
    /// <param name="chunkId"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    bool AccessCodeChunk(Address address, UInt256 chunkId, ref long gasAvailable, bool isWrite);

    bool AccessForAbsentAccount(Address address, ref long gasAvailable);

    /// <summary>
    ///     When you have to access the complete account
    /// </summary>
    /// <param name="address"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    bool AccessCompleteAccount(Address address, ref long gasAvailable, bool isWrite = false);

    bool AccessForSelfDestruct(Address contract, Address inheritor, ref long gasAvailable, bool balanceIsZero, bool inheritorExist);
    byte[][] GetAccessedKeys();
}
