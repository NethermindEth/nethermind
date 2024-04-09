// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Witness;

public class NoExecWitness: IExecutionWitness
{
    public long AccessForContractCreationInit(Address contractAddress, bool isValueTransfer) => 0;

    public long AccessForContractCreated(Address contractAddress)=> 0;

    public long AccessForTransaction(Address originAddress, Address? destinationAddress, bool isValueTransfer) => 0;

    public long AccessForGasBeneficiary(Address gasBeneficiary) => 0;

    public long AccessForCodeOpCodes(Address caller) => 0;

    public long AccessForBalance(Address address, bool isWrite = false) => 0;

    public long AccessForCodeHash(Address address) => 0;

    public long AccessForStorage(Address address, UInt256 key, bool isWrite) => 0;

    public long AccessForCodeProgramCounter(Address address, int programCounter, bool isWrite) => 0;

    public bool AccessAndChargeForCodeSlice(Address address, int startIncluded, int endNotIncluded, bool isWrite,
        ref long unspentGas) => true;

    public long AccessCodeChunk(Address address, byte chunkId, bool isWrite) => 0;

    public long AccessForAbsentAccount(Address address) => 0;

    public long AccessCompleteAccount(Address address, bool isWrite = false) => 0;

    public long AccessForSelfDestruct(Address contract, Address inheritor, bool balanceIsZero) => 0;

    public byte[][] GetAccessedKeys() => Array.Empty<byte[]>();
}
