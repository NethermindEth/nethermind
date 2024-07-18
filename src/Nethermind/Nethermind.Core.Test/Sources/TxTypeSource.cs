// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Test.Sources;

public class TxTypeSource
{
    public static IEnumerable<TxType> Any
    {
        get
        {
            yield return 0;
            yield return (TxType)15;
            yield return (TxType)16;
            yield return (TxType)255;
            foreach (TxType existing in Existing)
                yield return existing;
        }
    }

    public static IEnumerable<TxType> Existing
    {
        get
        {
            yield return TxType.Legacy;
            yield return TxType.AccessList;
            yield return TxType.EIP1559;
            yield return TxType.Blob;
        }
    }
}
