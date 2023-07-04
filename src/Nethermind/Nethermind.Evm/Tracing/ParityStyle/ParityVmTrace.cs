// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing.ParityStyle
{
    public class ParityVmTrace
    {
        public byte[] Code { get; set; }
        public ParityVmOperationTrace[] Operations { get; set; }
    }
}
