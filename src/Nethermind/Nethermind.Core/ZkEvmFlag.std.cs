// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>Whether this is the zkEVM guest build.</summary>
/// <remarks>
/// Code that both builds compile tests this flag instead of <c>#if ZK_EVM</c>: it is a constant in each build, so the
/// JIT and the guest's ahead-of-time compiler fold the branch it guards, and the guest compiles none of the paths
/// it cannot run, such as parallel block execution. See <c>ZkEvmFlag.zkevm.cs</c>.
/// </remarks>
public readonly partial struct ZkEvmFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
