// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

public class Db(IWorldState worldState)
{
    public IWorldState WorldState { get; } = worldState;

    public IJavaScriptObject getBalance(object address) => WorldState.GetBalance(address.ToAddress()).ToBigInteger();

    public ulong getNonce(object address) => WorldState.GetNonce(address.ToAddress());

    public ITypedArray<byte> getCode(object address) => WorldState.GetCode(address.ToAddress()).ToTypedScriptArray();

    public ITypedArray<byte> getState(object address, object index)
    {
        using ArrayPoolDisposableReturn handle = ArrayPoolDisposableReturn.Rent(32, out byte[] array);

        // Geth's db.getState(addr, key) takes the raw storage slot (the preimage) and reads it live from the
        // executing state, hashing to the trie key internally (see go-ethereum eth/tracers/js/goja.go
        // dbObj.GetState -> StateDB.GetState). Mirror that: treat the JS argument as the slot index and read
        // through the normal storage path, which resolves in-flight writes and computes Keccak(index) for the
        // trie key. Any remaining semantic differences from Geth's tracer are accepted rather than special-cased.
        ReadOnlySpan<byte> bytes = WorldState.Get(new StorageCell(address.ToAddress(), new UInt256(index.ToBytes(), isBigEndian: true)));
        if (bytes.Length < array.Length)
        {
            Array.Clear(array);
        }
        bytes.CopyTo(array.AsSpan(array.Length - bytes.Length));
        return array.ToTypedScriptArray();
    }

    public bool exists(object address) => WorldState.TryGetAccount(address.ToAddress(), out AccountStruct account) && !account.IsTotallyEmpty;
}
