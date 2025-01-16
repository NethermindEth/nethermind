// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Healing;

public interface ITrieNodeRecovery<in TRequest>
{
    bool CanRecover { get; }
    Task<byte[]?> Recover(ValueHash256 rlpHash, TRequest request);
}
