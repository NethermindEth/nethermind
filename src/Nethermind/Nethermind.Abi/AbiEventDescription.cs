// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Abi
{
    public class AbiEventDescription : AbiBaseDescription<AbiEventParameter>
    {
        public bool Anonymous { get; set; }
    }
}
