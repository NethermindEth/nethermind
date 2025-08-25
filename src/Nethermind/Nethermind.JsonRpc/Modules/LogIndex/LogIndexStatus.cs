// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules.LogIndex;

// TODO: add forward/backward sync status?
public record LogIndexStatus(int? FromBlock, int? ToBlock, string DbSize);
