// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Queue = Nethermind.Evm.EvmObjectPool<Nethermind.Evm.ExecutionEnvironment>;

namespace Nethermind.Evm;

public sealed partial class ExecutionEnvironment
{
    private static readonly Queue _pool = new();
}
