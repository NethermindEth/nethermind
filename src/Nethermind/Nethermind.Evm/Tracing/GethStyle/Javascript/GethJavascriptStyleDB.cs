// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class GethJavascriptStyleDb
{
    private readonly IWorldState _stateRepository;

    public GethJavascriptStyleDb(IWorldState stateRepository)
    {
        _stateRepository = stateRepository;
    }

    public UInt256 getBalance(Address address)
    {

        return _stateRepository.GetBalance(address);
    }

    public UInt256 getNonce(Address address)
    {

        return _stateRepository.GetNonce(address);
    }

    public byte[] getCode(Address address)
    {
        return _stateRepository.GetCode(address);
    }

    public byte[] getState(Address address, Keccak hash)
    {
        return _stateRepository.Get(new StorageCell(address, new UInt256(hash.Bytes)));
    }

    public bool exists(Address address)
    {
        return !_stateRepository.GetAccount(address).IsTotallyEmpty;
    }
}
