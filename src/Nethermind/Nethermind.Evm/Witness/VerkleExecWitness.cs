// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Verkle.Tree;

namespace Nethermind.Evm.Witness;

public class VerkleExecWitness: IWitness
{
    private readonly VerkleWitness _witness = new VerkleWitness();

    public bool AccessAndChargeForContractCreationInit(Address contractAddress, bool isValueTransfer, ref long unspentGas)
    {
        long gas = _witness.AccessForContractCreationInit(contractAddress, isValueTransfer);
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForContractCreated(Address contractAddress, ref long unspentGas)
    {
        long gas = _witness.AccessContractCreated(contractAddress);
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForTransaction(Address originAddress, Address? destinationAddress, bool isValueTransfer,
        ref long unspentGas)
    {
        long gas = _witness.AccessForTransaction(originAddress, destinationAddress, isValueTransfer);
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForGasBeneficiary(Address gasBeneficiary, ref long unspentGas)
    {
        long gas = _witness.AccessForGasBeneficiary(gasBeneficiary);
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForCodeOpCodes(Address caller, ref long unspentGas)
    {
        long gas = _witness.AccessForCodeOpCodes(caller);
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForBalance(Address address, ref long unspentGas)
    {
        long gas = _witness.AccessBalance(address);
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForCodeHash(Address address, ref long unspentGas)
    {
        long gas = _witness.AccessCodeHash(address);
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForStorage(Address address, UInt256 key, bool isWrite, ref long unspentGas)
    {
        long gas = _witness.AccessStorage(address, key, isWrite);
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForCodeProgramCounter(Address address, int programCounter, bool isWrite, ref long unspentGas)
    {
        long gas = _witness.AccessCodeChunk(address, CalculateCodeChunkIdFromPc(programCounter), isWrite);
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForCodeSlice(Address address, int start, int codeSliceLength, bool isWrite, ref long unspentGas)
    {
        byte startChunkId = CalculateCodeChunkIdFromPc(start);
        byte endChunkId = CalculateCodeChunkIdFromPc(start + codeSliceLength);

        for (byte ch = startChunkId; ch <= endChunkId; ch++)
        {
            long gas = _witness.AccessCodeChunk(address, ch, false);
            if (!UpdateGas(gas, ref unspentGas)) return false;
        }
        return true;
    }


    public bool AccessAndChargeForAbsentAccount(Address address, ref long unspentGas)
    {
        long gas = _witness.AccessForProofOfAbsence(address);
        return UpdateGas(gas, ref unspentGas);
    }

    private static bool UpdateGas(long gasCost, ref long gasAvailable)
    {
        if (gasAvailable < gasCost) return false;
        gasAvailable -= gasCost;
        return true;
    }

    private static byte CalculateCodeChunkIdFromPc(int pc)
    {
        int chunkId = pc / 31;
        return (byte)chunkId;
    }
}
