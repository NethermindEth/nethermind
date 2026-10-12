// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>
/// The EIP-8038 setting the opcode table has chosen for a handler.
/// </summary>
/// <remarks>
/// The rules that switch together with EIP-8038 take this setting as their type parameter. Only
/// <see cref="Eip8038On"/> and <see cref="Eip8038Off"/> implement the interface, so anchoring one of
/// those rules on any other flag does not compile.
/// </remarks>
internal interface IEip8038Flag : IFlag;

/// <summary>EIP-8038 is active.</summary>
internal readonly struct Eip8038On : IEip8038Flag
{
    /// <inheritdoc />
    public static bool IsActive => true;
}

/// <summary>EIP-8038 is inactive.</summary>
internal readonly struct Eip8038Off : IEip8038Flag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
