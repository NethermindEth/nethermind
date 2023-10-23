// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.Serialization;

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

    protected EraFormatException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}
