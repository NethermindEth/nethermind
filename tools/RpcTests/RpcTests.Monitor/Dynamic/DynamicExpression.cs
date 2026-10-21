// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DynamicExpresso;

namespace Nethermind.RpcTests.Monitor.Dynamic;

internal class DynamicExpression<TContext, TResult>(string expression)
{
    private readonly Lambda _lambda = DynamicBinder<TContext>.CreateInterpreter().Parse(expression, DynamicBinder<TContext>.Parameters);
    public TResult Compile(TContext value) => (TResult)_lambda.Invoke(DynamicBinder<TContext>.GetArgs(value));
}
