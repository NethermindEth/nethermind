// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.Serialization;

namespace Nethermind.Synchronization;
public class EraImportException : Exception
{
    public EraImportException()
    {
    }

    public EraImportException(string? message) : base(message)
    {
    }

    public EraImportException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
