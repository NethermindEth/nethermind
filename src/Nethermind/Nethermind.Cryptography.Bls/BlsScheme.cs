// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Cryptography
{
    public enum BlsScheme
    {
        Unknown = 0,
        Basic,
        MessageAugmentation,
        ProofOfPossession
    }
}
