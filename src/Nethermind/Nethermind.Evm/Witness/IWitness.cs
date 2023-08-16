// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Witness;

public interface IWitness
{
    public bool AccessAndChargeForContractCreationInit(Address contractAddress, bool isValueTransfer,
        ref long unspentGas);

    public bool AccessAndChargeForContractCreated(Address contractAddress, ref long unspentGas);

    public bool AccessAndChargeForTransaction(Address originAddress, Address? destinationAddress, bool isValueTransfer,
        ref long unspentGas);

    public bool AccessAndChargeForGasBeneficiary(Address gasBeneficiary, ref long unspentGas);


    public bool AccessAndChargeForCodeOpCodes(Address caller, ref long unspentGas);


    public bool AccessAndChargeForBalance(Address address, ref long unspentGas);

    public bool AccessAndChargeForCodeHash(Address address, ref long unspentGas);

    public bool AccessAndChargeForStorage(Address address, UInt256 key, bool isWrite, ref long unspentGas);

    public bool AccessAndChargeForCodeProgramCounter(Address address, int programCounter, bool isWrite, ref long unspentGas);

    public bool AccessAndChargeForAbsentAccount(Address address, ref long unspentGas);
}
