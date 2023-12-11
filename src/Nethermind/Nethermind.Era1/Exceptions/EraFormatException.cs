// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;
[Serializable]
internal class EraFormatException : EraException
{
    public EraFormatException()
    {
    }

    public EraFormatException(string message) : base(message)
    {
    }

    public EraFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
