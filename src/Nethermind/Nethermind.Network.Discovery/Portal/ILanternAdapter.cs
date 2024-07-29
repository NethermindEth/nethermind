// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;

namespace Nethermind.Network.Discovery.Portal;

public interface ILanternAdapter
{
    Task<byte[]?> OnMsgReq(IEnr sender, TalkReqMessage message);
    void OnMsgResp(IEnr sender, TalkRespMessage message);
    IPortalContentNetwork RegisterContentNetwork(byte[] networkId, IPortalContentNetwork.Store store);
}

public interface IPortalContentNetwork
{
    public Task<byte[]?> LookupContent(byte[] contentKey, CancellationToken token);

    public void AddSeed(IEnr node);
    public Task Run(CancellationToken token);
    public Task Bootstrap(CancellationToken token);

    public interface Store
    {
        public byte[]? GetContent(byte[] contentKey);
    }
}
