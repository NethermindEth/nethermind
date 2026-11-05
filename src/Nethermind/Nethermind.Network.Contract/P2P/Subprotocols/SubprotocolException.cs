// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

#pragma warning disable IDE0130 // Lives here so sync can tell peer protocol faults apart; keeps its original namespace.
namespace Nethermind.Network.P2P.Subprotocols
{
    public class SubprotocolException(string message) : Exception(message)
    {
    }
}
