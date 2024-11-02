// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Evm.T8n.JsonTypes;

public class Ommer(int delta, Address address)
{
    public int Delta { get; set; } = delta;
    public Address Address { get; set; } = address;
}
