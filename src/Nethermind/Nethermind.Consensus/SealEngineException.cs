// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus
{
    public class SealEngineException : Exception
    {
        public SealEngineException(string message) : base(message)
        {
        }
    }
}
