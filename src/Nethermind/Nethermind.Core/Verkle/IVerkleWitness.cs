// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Int256;

namespace Nethermind.Core;

public interface IVerkleWitness : IJournal<int>
{
    public byte[][] GetAccessedKeys();
    public long AccessForCodeOpCodes(Address caller);
    public long AccessValueTransfer(Address caller, Address callee);

    public long AccessForContractCreationInit(Address contractAddress, bool isValueTransfer);

    public long AccessContractCreated(Address contractAddress);

    public long AccessBalance(Address address);

    public long AccessCodeHash(Address address);

    public long AccessStorage(Address address, UInt256 key, bool isWrite);

    public long AccessCodeChunk(Address address, byte chunkId, bool isWrite);

    public long AccessCompleteAccount(Address address, bool isWrite = false);

    public long AccessForTransaction(Address originAddress, Address destinationAddress, bool isValueTransfer);
    public long AccessForProofOfAbsence(Address address);
}
