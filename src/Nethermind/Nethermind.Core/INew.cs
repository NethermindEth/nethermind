// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public interface INew<in TArg, out T>
{
    public static abstract T New(TArg arg);
}
