// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc;

internal interface IPostAcknowledgementResponse
{
    IPostAcknowledgementActivation TakeActivation();
}

internal interface IPostAcknowledgementActivation
{
    void Activate();
    void Abort();
}
