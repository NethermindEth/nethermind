// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.State.Healing;

public interface ICodeRecovery
{
    Task<byte[]?> Recover(byte[] key, CancellationToken cancellationToken = default);
}
