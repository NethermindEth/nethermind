// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EthStats
{
    public interface IMessageBuilder<out T> where T : IMessage
    {
        T Build(params object[] args);
    }
}
