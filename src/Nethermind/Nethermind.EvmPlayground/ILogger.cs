// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.EvmPlayground
{
    public interface ILogger
    {
        void Info(string text);
        void Warn(string text);
        void Error(string text, Exception ex = null);
    }
}
