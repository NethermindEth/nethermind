// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Evm.Tracing;

public interface ITxLogsMutator
{
    bool IsMutatingLogs { get; }

    void SetLogsToMutate(ICollection<LogEntry> logsToMutate);
}
