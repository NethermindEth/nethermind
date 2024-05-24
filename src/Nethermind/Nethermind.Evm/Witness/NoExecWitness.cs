// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Witness;

public class NoExecWitness: IExecutionWitness
{
    public bool AccessForContractCreationInit(Address contractAddress, ref long gasAvailable, bool isValueTransfer) =>
        true;

    public bool AccessForContractCreated(Address contractAddress, ref long gasAvailable) => true;

    public bool AccessForTransaction(Address originAddress, Address? destinationAddress, ref long gasAvailable,
        bool isValueTransfer) => true;

    public bool AccessForGasBeneficiary(Address gasBeneficiary, ref long gasAvailable) => true;

    public bool AccessForCodeOpCodes(Address caller, ref long gasAvailable) => true;

    public bool AccessForBalance(Address address, ref long gasAvailable, bool isWrite = false) => true;

    public bool AccessForCodeHash(Address address, ref long gasAvailable) => true;

    public bool AccessForStorage(Address address, UInt256 key, ref long gasAvailable, bool isWrite) => true;

    public bool AccessForCodeProgramCounter(Address address, int programCounter, ref long gasAvailable, bool isWrite) =>
        true;

    public bool AccessAndChargeForCodeSlice(Address address, int startIncluded, int endNotIncluded,
        ref long gasAvailable, bool isWrite) => true;

    public bool AccessCodeChunk(Address address, UInt256 chunkId, ref long gasAvailable, bool isWrite) => true;

    public bool AccessForAbsentAccount(Address address, ref long gasAvailable) => true;

    public bool AccessCompleteAccount(Address address, ref long gasAvailable, bool isWrite = false) => true;

    public bool AccessForSelfDestruct(Address contract, Address inheritor, ref long gasAvailable, bool balanceIsZero,
        bool inheritorExist) => true;
    public byte[][] GetAccessedKeys() => Array.Empty<byte[]>();
}
