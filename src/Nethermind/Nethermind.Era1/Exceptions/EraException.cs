// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

public class EraException : Exception
{
    public EraException() { }
    public EraException(string message) : base(message) { }
    public EraException(string message, Exception inner) : base(message, inner) { }

}
