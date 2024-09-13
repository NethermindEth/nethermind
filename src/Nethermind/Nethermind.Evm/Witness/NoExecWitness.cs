// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Witness;

public class NoExecWitness : IExecutionWitness
{
    public bool AccessForContractCreationInit(Address contractAddress, ref long gasAvailable) =>
        true;

    public bool AccessForContractCreated(Address contractAddress, ref long gasAvailable) => true;

    public bool AccessForTransaction(Address originAddress, Address? destinationAddress,
        bool isValueTransfer) => true;

    public bool AccessForGasBeneficiary(Address gasBeneficiary) => true;

    public bool AccessAccountData(Address caller, ref long gasAvailable) => true;

    public bool AccessForBalanceOpCode(Address address, ref long gasAvailable) => true;

    public bool AccessCodeHash(Address address, ref long gasAvailable) => true;

    public bool AccessForStorage(Address address, UInt256 key, bool isWrite, ref long gasAvailable) => true;
    public bool AccessForBlockHashOpCode(Address address, UInt256 key, ref long gasAvailable) => true;
    public bool AccessForCodeProgramCounter(Address address, int programCounter, ref long gasAvailable) =>
        true;

    public bool AccessAndChargeForCodeSlice(Address address, int startIncluded, int endNotIncluded, bool isWrite, ref long gasAvailable) => true;

    public bool AccessCodeChunk(Address address, UInt256 chunkId, bool isWrite, ref long gasAvailable) => true;

    public bool AccessForAbsentAccount(Address address, ref long gasAvailable) => true;

    public bool AccessCompleteAccount(Address address, ref long gasAvailable, bool isWrite = false) => true;
    public bool AccessAccountForWithdrawal(Address address) => true;
    public bool AccessForBlockhashInsertionWitness(Address address, UInt256 key) => true;
    public bool AccessForSelfDestruct(Address contract, Address inheritor, bool balanceIsZero,
        bool inheritorExist, ref long gasAvailable) => true;
    public byte[][] GetAccessedKeys() => Array.Empty<byte[]>();
    public bool AccessForValueTransfer(Address from, Address to, ref long gasAvailable) => true;
    public bool AccessForContractCreationCheck(Address contractAddress, ref long gasAvailable) => true;
}
