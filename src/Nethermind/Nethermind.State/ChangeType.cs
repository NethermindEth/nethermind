// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

internal enum ChangeType
{
    Null = 0,
    JustCache,
    Touch,
    Update,
    New,
    Delete,
    RecreateEmpty,
}
