// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.Eip7928;

// Computed on access, so the guest runs no class constructor for this type.
public partial class CodeChangeDecoder
{
    private static RlpLimit _codeLimit => new(Eip7928Constants.MaxCodeSize, "", ReadOnlyMemory<char>.Empty);
}
