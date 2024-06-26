// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7702;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Core.Test.Encoding;

[TestFixture]
public class AuthorizationListDecoderTests
{
    public void Decode_()
    {

        RlpStream rlpStream = new RlpStream(32 * 4);


        AuthorizationTupleDecoder sut = new();


    }

}
