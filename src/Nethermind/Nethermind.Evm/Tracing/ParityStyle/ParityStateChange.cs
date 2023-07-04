// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing.ParityStyle
{
    public class ParityStateChange<T>
    {
        public ParityStateChange(T before, T after)
        {
            Before = before;
            After = after;
        }

        public T Before { get; set; }
        public T After { get; set; }
    }
}
