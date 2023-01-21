// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Transactions;

namespace Nethermind.Blockchain.Find;

public class ResourceNotFoundException : ArgumentException
{
    public ResourceNotFoundException(string message) : base(message)
    {
    }
}
