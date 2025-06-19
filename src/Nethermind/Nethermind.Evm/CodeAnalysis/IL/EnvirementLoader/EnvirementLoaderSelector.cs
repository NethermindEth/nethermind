// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.CodeAnalysis.IL.Delegates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL.EnvirementLoader
{
    internal class EnvirementLoaderSelector<TDelegate>
    {
        public static IEnvirementLoader EnvirementLoader
        {
            get
            {
                if (typeof(TDelegate) == typeof(ILEmittedInternalMethod))
                {
                    return EnvirementLoaderInternalMethod.Instance;
                }
                else if (typeof(TDelegate) == typeof(ILEmittedEntryPoint))
                {
                    return EnvirementLoaderEntryPoint.Instance;
                }
                else
                {
                    throw new NotSupportedException($"Envirement loader for {typeof(TDelegate).Name} is not supported.");
                }
            }
        }
    }
}
