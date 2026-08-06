// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-7997">EIP-7997</see> parameters.
/// </summary>
public static class Eip7997Constants
{
    /// <summary>
    /// The fixed address of the deterministic deployment factory.
    /// </summary>
    public static readonly Address FactoryAddress = new("0x4e59b44847b379578588920cA78FbF26c0B4956C");

    /// <summary>
    /// The canonical runtime bytecode installed at <see cref="FactoryAddress"/> on the fork activation block.
    /// </summary>
    public static readonly byte[] Code = Bytes.FromHexString("7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe03601600081602082378035828234f58015156039578182fd5b8082525050506014600cf3");
}
