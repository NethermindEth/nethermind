// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Sigil.NonGeneric;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// Represents the IL-EVM information about the contract.
/// </summary>
internal class ILInfo
{
    /// <summary>
    /// Represents an information about IL-EVM being not able to optimize the given <see cref="CodeInfo"/>.
    /// </summary>
    public static readonly ILInfo NoIlEVM = new();

    public ushort[] Pcs;
    public Emit[] IlMethod;

    Emit HasOverride(ushort pc)
    {
        return IlMethod[pc];
    }
}
