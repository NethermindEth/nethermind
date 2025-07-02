// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.MessageProvider;

public interface IMessageProvider<out T>
{
    IAsyncEnumerable<T> Messages { get; }
}
