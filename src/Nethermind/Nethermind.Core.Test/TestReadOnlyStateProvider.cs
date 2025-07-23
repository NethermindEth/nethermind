// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Core.Test;

public class TestReadOnlyStateProvider: IReadOnlyStateProvider
{
    private Dictionary<Address, AccountStruct> _accounts = new();
    private Dictionary<ValueHash256, byte[]> _codes = new();

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        return _accounts.TryGetValue(address, out account);
    }

    public Hash256 StateRoot => throw new NotImplementedException();
    public byte[]? GetCode(Address address)
    {
        if (TryGetAccount(address, out AccountStruct account)) return _codes[account.CodeHash];
        return null;
    }

    public byte[]? GetCode(in ValueHash256 codeHash)
    {
        return _codes[codeHash];
    }

    public bool IsContract(Address address)
    {
        return TryGetAccount(address, out AccountStruct account) && account.IsContract;
    }

    public bool AccountExists(Address address)
    {
        return TryGetAccount(address, out AccountStruct _);
    }

    public bool IsDeadAccount(Address address)
    {
        return !TryGetAccount(address, out AccountStruct account) || account.IsEmpty;
    }

    public void CreateAccount(Address address, UInt256 wei, UInt256 nonce = default)
    {
        _accounts[address] = new AccountStruct(nonce, wei);
    }


    public void InsertCode(Address address, Memory<byte>code, IReleaseSpec spec)
    {
        InsertCode(code.ToArray(), address);
    }

    public void InsertCode(byte[] code, Address address)
    {
        if (!_accounts.TryGetValue(address, out AccountStruct account))
        {
            account = new AccountStruct();
        }

        ValueHash256 codeHash = Keccak.Compute(code);
        account = new AccountStruct(account.Nonce, account.Balance, account.StorageRoot, codeHash);
        _codes[codeHash] = code;
        _accounts[address] = account;
    }

    public void IncrementNonce(Address address)
    {
        if (!_accounts.TryGetValue(address, out AccountStruct account))
        {
            account = new AccountStruct();
        }

        account = new AccountStruct(account.Nonce + 1, account.Balance, account.StorageRoot, account.CodeHash);
        _accounts[address] = account;
    }
}
