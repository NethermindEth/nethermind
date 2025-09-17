// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
public class QuorumCertificateManagerTest
{
    [TestCase]
    public void VerifyCertificate_()
    {
        var quorumCertificateManager = new QuorumCertificateManager(new XdcContext(), Substitute.For<IBlockTree>(), Substitute.For<IXdcReleaseSpec>(), Substitute.For<IEpochSwitchManager>());


    }
}
