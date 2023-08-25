// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Verkle.Tree;

namespace Nethermind.Evm.Witness;

public class VerkleExecWitness: IWitness
{
    private readonly ILogger _logger;
    private readonly VerkleWitness _witness;

    public VerkleExecWitness(ILogManager logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _witness = new VerkleWitness();
    }

    public bool AccessAndChargeForContractCreationInit(Address contractAddress, bool isValueTransfer, ref long unspentGas)
    {
        long gas = _witness.AccessForContractCreationInit(contractAddress, isValueTransfer);
        if(_logger.IsTrace) _logger.Trace($"AccessAndChargeForContractCreationInit: {gas} {contractAddress} {isValueTransfer} {unspentGas}");
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForContractCreated(Address contractAddress, ref long unspentGas)
    {
        long gas = _witness.AccessContractCreated(contractAddress);
        if(_logger.IsTrace) _logger.Trace($"AccessAndChargeForContractCreated: {gas} {contractAddress} {unspentGas}");
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForTransaction(Address originAddress, Address? destinationAddress, bool isValueTransfer,
        ref long unspentGas)
    {
        long gas = _witness.AccessForTransaction(originAddress, destinationAddress, isValueTransfer);
        if(_logger.IsTrace) _logger.Trace($"AccessAndChargeForTransaction: {gas} {originAddress} {destinationAddress} {isValueTransfer} {unspentGas}");
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForGasBeneficiary(Address gasBeneficiary, ref long unspentGas)
    {
        long gas = _witness.AccessForGasBeneficiary(gasBeneficiary);
        if(_logger.IsTrace) _logger.Trace($"AccessAndChargeForGasBeneficiary: {gas} {gasBeneficiary} {unspentGas}");
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForCodeOpCodes(Address caller, ref long unspentGas)
    {
        long gas = _witness.AccessForCodeOpCodes(caller);
        if(_logger.IsTrace) _logger.Trace($"AccessAndChargeForCodeOpCodes: {gas} {caller} {unspentGas}");
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForBalance(Address address, ref long unspentGas)
    {
        long gas = _witness.AccessBalance(address);
        if(_logger.IsTrace) _logger.Trace($"AccessAndChargeForBalance: {gas} {address} {unspentGas}");
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForCodeHash(Address address, ref long unspentGas)
    {
        long gas = _witness.AccessCodeHash(address);
        if(_logger.IsTrace) _logger.Trace($"AccessAndChargeForCodeHash: {gas} {address} {unspentGas}");
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForStorage(Address address, UInt256 key, bool isWrite, ref long unspentGas)
    {
        long gas = _witness.AccessStorage(address, key, isWrite);
        if(_logger.IsTrace) _logger.Trace($"AccessAndChargeForStorage: {gas} {address} {key} {isWrite} {unspentGas}");
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForCodeProgramCounter(Address address, int programCounter, bool isWrite, ref long unspentGas)
    {
        long gas = _witness.AccessCodeChunk(address, CalculateCodeChunkIdFromPc(programCounter), isWrite);
        if(_logger.IsTrace) _logger.Trace($"AccessAndChargeForCodeProgramCounter: {gas} {address} {programCounter} {isWrite} {unspentGas}");
        return UpdateGas(gas, ref unspentGas);
    }

    public bool AccessAndChargeForCodeSlice(Address address, int start, int codeSliceLength, bool isWrite, ref long unspentGas)
    {
        byte startChunkId = CalculateCodeChunkIdFromPc(start);
        byte endChunkId = CalculateCodeChunkIdFromPc(start + codeSliceLength);

        long accGas = 0;
        for (byte ch = startChunkId; ch <= endChunkId; ch++)
        {
            long gas = _witness.AccessCodeChunk(address, ch, false);
            accGas += gas;
            if (!UpdateGas(gas, ref unspentGas)) return false;
        }
        if(_logger.IsTrace) _logger.Trace($"AccessAndChargeForCodeSlice: {accGas} {start} {start + codeSliceLength} {isWrite} {unspentGas}");
        return true;
    }


    public bool AccessAndChargeForAbsentAccount(Address address, ref long unspentGas)
    {
        long gas = _witness.AccessForProofOfAbsence(address);
        if(_logger.IsTrace) _logger.Trace($"AccessAndChargeForAbsentAccount: {gas} {address} {unspentGas}");
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

    public byte[][] GetAccessedKeys() =>  _witness.GetAccessedKeys();
}
