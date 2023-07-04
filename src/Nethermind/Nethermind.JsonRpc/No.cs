// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Authentication;

namespace Nethermind.JsonRpc;

public class No
{
    public static IRpcAuthentication Authentication = NoAuthentication.Instance;
}
