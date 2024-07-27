// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Merge.AuRa.Shutter;
public interface IShutterTxSignal
{
    Task WaitForTransactions(ulong slot);
}
