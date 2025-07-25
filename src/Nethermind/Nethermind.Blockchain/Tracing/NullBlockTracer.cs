// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain.Tracing;

public sealed class NullBlockTracer : BlockTracer
{
    public static NullBlockTracer Instance { get; } = new NullBlockTracer();
    public override ITxTracer StartNewTxTrace(Transaction? tx) => NullTxTracer.Instance;
}
