// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Exceptions;

public class ConcurrencyLimitReachedException : InvalidOperationException
{
    public ConcurrencyLimitReachedException(string message) : base(message)
    {
    }
}
