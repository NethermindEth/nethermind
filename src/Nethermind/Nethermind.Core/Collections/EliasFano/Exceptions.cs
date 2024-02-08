// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;

namespace Nethermind.Core.Collections.EliasFano;

public class EliasFanoExceptions : Exception
{
    protected EliasFanoExceptions(){}
    protected EliasFanoExceptions(string message, Exception? inner = null) : base(message, inner) { }
}

public class EliasFanoBuilderException: EliasFanoExceptions
{
    public List<ulong>? Shard { get; set; }

    public EliasFanoBuilderException(){}
    public EliasFanoBuilderException(string message, Exception? inner = null) : base(message, inner) { }
}

public class EliasFanoQueryException: EliasFanoExceptions
{
    public List<ulong>? Shard { get; set; }

    public EliasFanoQueryException(){}
    public EliasFanoQueryException(string message, Exception? inner = null) : base(message, inner) { }
}
