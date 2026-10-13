// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public sealed partial class ExecutionEnvironment
{
    private static EvmObjectPool<ExecutionEnvironment> _pool => field ??= new();
}
