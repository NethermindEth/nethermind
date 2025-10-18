// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;

public class ReceiptsInnerMessage69(IOwnedReadOnlyList<TxReceipt[]> txReceipts) : V63.Messages.ReceiptsMessage(txReceipts);
