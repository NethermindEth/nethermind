// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Witness;

public class NoExecWitness: IWitness
{
    public bool AccessAndChargeForContractCreationInit(Address contractAddress, bool isValueTransfer, ref long unspentGas) => true;

    public bool AccessAndChargeForContractCreated(Address contractAddress, ref long unspentGas) => true;

    public bool AccessAndChargeForTransaction(Address originAddress, Address? destinationAddress, bool isValueTransfer,
        ref long unspentGas) => true;

    public bool AccessAndChargeForGasBeneficiary(Address gasBeneficiary, ref long unspentGas) => true;
    public bool AccessAndChargeForCodeOpCodes(Address caller, ref long unspentGas)
    {
        return true;
    }

    public bool AccessAndChargeForBalance(Address address, ref long unspentGas)
    {
        return true;
    }

    public bool AccessAndChargeForCodeHash(Address address, ref long unspentGas)
    {
        return true;
    }

    public bool AccessAndChargeForStorage(Address address, UInt256 key, bool isWrite, ref long unspentGas)
    {
        return true;
    }

    public bool AccessAndChargeForCodeProgramCounter(Address address, int programCounter, bool isWrite, ref long unspentGas)
    {
        return true;
    }

    public bool AccessAndChargeForAbsentAccount(Address address, ref long unspentGas)
    {
        return true;
    }

    public bool AccessAndChargeForCodeSlice(Address address, int start, int codeSliceLength, bool isWrite, ref long unspentGas)
    {
        return true;
    }

    public byte[][] GetAccessedKeys() => Array.Empty<byte[]>();
}
