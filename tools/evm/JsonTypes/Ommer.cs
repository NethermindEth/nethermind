// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Evm.JsonTypes;

public class Ommer
{
    public int Delta { get; set; }
    public Address Address { get; set; }
}
