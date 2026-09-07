// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>One logical flat-state entry to include in a rebuilt EIP-8297 tree.</summary>
public readonly record struct RebuildEntry
{
    internal enum EntryKind { Account, Storage, Code }
    internal EntryKind Kind { get; private init; }
    internal ValueHash256 Hash { get; private init; }
    internal Account? Account { get; private init; }
    internal PbtFullKey Key { get; private init; }
    internal EvmWord Slot { get; private init; }
    internal CodeInfo? Code { get; private init; }

    /// <summary>Creates an account entry keyed by its already-hashed address.</summary>
    public static RebuildEntry FromAccount(ValueHash256 addressHash, Account? account) =>
        new() { Kind = EntryKind.Account, Hash = addressHash, Account = account };

    /// <summary>Creates a storage entry with its complete PBT storage key.</summary>
    public static RebuildEntry FromSlot(PbtFullKey key, EvmWord slot) =>
        new() { Kind = EntryKind.Storage, Key = key, Slot = slot };

    /// <summary>Creates a whole-code entry keyed by its content hash.</summary>
    public static RebuildEntry FromCode(ValueHash256 codeHash, CodeInfo code) =>
        new() { Kind = EntryKind.Code, Hash = codeHash, Code = code };
}
