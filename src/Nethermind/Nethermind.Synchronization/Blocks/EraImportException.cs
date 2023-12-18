// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Era1;
using System;

namespace Nethermind.Synchronization;
internal class EraImportException : EraException
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
