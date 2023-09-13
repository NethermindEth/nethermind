// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Nethermind.Evm;
using Nethermind.Core;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethJavascriptStyleDB
{

    private readonly IWorldState _stateRepository;

    public GethJavascriptStyleDB(IWorldState stateRepository)
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

    // public byte[] getState(Address address, Keccak hash)
    // {
    //     return _stateRepository.GetCodeOrEmpty(address, hash);
    // }

    public bool exists(Address address)
    {
        return _stateRepository.IsContract(address);
    }
}
