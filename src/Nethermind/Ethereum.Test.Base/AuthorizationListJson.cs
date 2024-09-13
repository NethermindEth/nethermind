// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ethereum.Test.Base;
public class AuthorizationListJson
{
    public string ChainId { get; set; }
    public string Address { get; set; }
    public string Nonce { get; set; }
    public string V { get; set; }
    public string R { get; set; }
    public string S { get; set; }
    public string Signer { get; set; }
}
