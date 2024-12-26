// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public readonly struct PagesStats
{
    public required long PagesAllocated { get; init; }
    public required long PagesTaken { get; init; }
    public required long PagesReturned { get; init; }
    public required long PagesReused { get; init; }
    public required long AllocatedPagesPending { get; init; }
    public required long ReturnedPagesPending { get; init; }
}
