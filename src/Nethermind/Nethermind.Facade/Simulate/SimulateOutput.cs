// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Proxy.Models.Simulate;

namespace Nethermind.Facade.Simulate;

public class SimulateOutput<TTrace>
{
    public string? Error { get; set; }
    public bool IsInvalidInput { get; set; }
    public TransactionResult TransactionResult { get; set; }

    public IReadOnlyList<SimulateBlockResult<TTrace>> Items { get; init; }
}
