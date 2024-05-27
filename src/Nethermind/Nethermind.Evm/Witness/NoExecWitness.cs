// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Witness;

public class NoExecWitness: IExecutionWitness
{
    public bool AccessForContractCreationInit(Address contractAddress, bool isValueTransfer, ref long gasAvailable) =>
        true;

    public bool AccessForContractCreated(Address contractAddress, ref long gasAvailable) => true;

    public bool AccessForTransaction(Address originAddress, Address? destinationAddress,
        bool isValueTransfer, ref long gasAvailable) => true;

    public bool AccessForGasBeneficiary(Address gasBeneficiary, ref long gasAvailable) => true;

    public bool AccessForCodeOpCodes(Address caller, ref long gasAvailable) => true;

    public bool AccessForBalance(Address address, ref long gasAvailable, bool isWrite = false) => true;

    public bool AccessForCodeHash(Address address, ref long gasAvailable) => true;

    public bool AccessForStorage(Address address, UInt256 key, bool isWrite, ref long gasAvailable) => true;

    public bool AccessForCodeProgramCounter(Address address, int programCounter, bool isWrite, ref long gasAvailable) =>
        true;

    public bool AccessAndChargeForCodeSlice(Address address, int startIncluded, int endNotIncluded, bool isWrite, ref long gasAvailable) => true;

    public bool AccessCodeChunk(Address address, UInt256 chunkId, bool isWrite, ref long gasAvailable) => true;

    public bool AccessForAbsentAccount(Address address, ref long gasAvailable) => true;

    public bool AccessCompleteAccount(Address address, ref long gasAvailable, bool isWrite = false) => true;

    public bool AccessForSelfDestruct(Address contract, Address inheritor, bool balanceIsZero,
        bool inheritorExist, ref long gasAvailable) => true;
    public byte[][] GetAccessedKeys() => Array.Empty<byte[]>();
}
