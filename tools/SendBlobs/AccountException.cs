// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace SendBlobs;
internal class AccountException : Exception
{
    public AccountException()
    {
    }

    public AccountException(string? message) : base(message)
    {
    }

    public AccountException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
