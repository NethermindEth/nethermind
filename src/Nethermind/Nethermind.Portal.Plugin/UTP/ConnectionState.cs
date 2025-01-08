// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Portal.UTP;

enum ConnectionState
{
    CsUnInitialized,
    CsSynSent,
    CsSynRecv,
    CsConnected,
    CsEnded,
}
