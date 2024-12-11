// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;
public class EraImportException : EraException
{
    public EraImportException()
    {
    }

    public EraImportException(string message) : base(message)
    {
    }

    public EraImportException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
