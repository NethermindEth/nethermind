// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.Find;

public class ResourceNotFoundException : ArgumentException
{
    public ResourceNotFoundException(string message) : base(message)
    {
    }
}
