// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>
/// Adapts <see cref="IWorldState"/> to <see cref="IReadOnlyStateProvider"/> without going through BAL-tracking decorators.
/// This preserves direct world-state semantics for speculative block-production reads while avoiding access-list pollution.
/// </summary>
public class WorldStateReadOnlyView(IWorldState worldState) : IReadOnlyStateProvider
{
    private readonly IWorldState _worldState = worldState;

    public Hash256 StateRoot => _worldState.StateRoot;

    public bool TryGetAccount(Address address, out AccountStruct account) => _worldState.TryGetAccount(address, out account);

    public byte[]? GetCode(Address address) => _worldState.GetCode(address);

    public byte[]? GetCode(in ValueHash256 codeHash) => _worldState.GetCode(codeHash);

    public bool IsContract(Address address) => _worldState.IsContract(address);

    public bool AccountExists(Address address) => _worldState.AccountExists(address);

    public bool IsDeadAccount(Address address) => _worldState.IsDeadAccount(address);

    public UInt256 GetNonce(Address address) => _worldState.GetNonce(address);

    public UInt256 GetBalance(Address address) => _worldState.GetBalance(address);

    public ValueHash256 GetCodeHash(Address address) => _worldState.GetCodeHash(address);

    public bool HasCode(Address address) => _worldState.HasCode(address);

    public bool IsStorageEmpty(Address address) => _worldState.IsStorageEmpty(address);
}
