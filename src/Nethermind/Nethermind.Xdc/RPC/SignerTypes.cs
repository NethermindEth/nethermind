// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Xdc;

public class SignerTypes
{
    public int CurrentNumber { get; set; }
    public Address[]? CurrentSigners { get; set; }
    public Address[]? MissingSigners { get; set; }
}
