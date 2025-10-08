// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

public interface ISignatureManager
{
    public ISigner CurrentSigner { get; }
    public ISignerStore CurrentSignerStore { get; }
    bool VerifyMessageSignature(Hash256 hash256, Signature signature, Address[] masternodes, out Address? address);
}
