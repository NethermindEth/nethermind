using System.Net;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Rlp;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;

namespace Lantern.Discv5.WireProtocol.Messages;

public class MessageDecoder(IIdentityManager identityManager, IEnrFactory enrFactory) : IMessageDecoder
{
    public Message DecodeMessage(byte[] message)
    {
        var messageType = (MessageType)message[0];

        return messageType switch
        {
            MessageType.Ping => DecodePingMessage(message),
            MessageType.Pong => DecodePongMessage(message),
            MessageType.FindNode => DecodeFindNodeMessage(message),
            MessageType.Nodes => DecodeNodesMessage(message),
            MessageType.TalkReq => DecodeTalkReqMessage(message),
            MessageType.TalkResp => DecodeTalkRespMessage(message),
            MessageType.RegTopic => DecodeRegTopicMessage(message),
            MessageType.RegConfirmation => DecodeRegConfirmationMessage(message),
            MessageType.Ticket => DecodeTicketMessage(message),
            MessageType.TopicQuery => DecodeTopicQueryMessage(message),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static ReadOnlySpan<Rlp.Rlp> GetMessageItems(byte[] message)
    {
        var decodedMessage = RlpDecoder.Decode(RlpDecoder.Decode(message.AsMemory(1))[0]);

        if ((decodedMessage[0].Source.Length - decodedMessage[0].PrefixLength) is 0 or > 8)
        {
            throw new Exception($"{nameof(Message.RequestId)} length is not within [1..8]");
        }

        return decodedMessage;
    }

    private static PingMessage DecodePingMessage(byte[] message)
    {
        var decodedMessage = GetMessageItems(message);

        return new PingMessage(RlpExtensions.ByteArrayToInt32(decodedMessage[1]))
        {
            RequestId = decodedMessage[0]
        };
    }

    private static PongMessage DecodePongMessage(byte[] message)
    {
        var decodedMessage = GetMessageItems(message);
        return new PongMessage(decodedMessage[0], (int)RlpExtensions.ByteArrayToInt64(decodedMessage[1]),
            new IPAddress(decodedMessage[2]), RlpExtensions.ByteArrayToInt32(decodedMessage[3]));
    }

    private static FindNodeMessage DecodeFindNodeMessage(byte[] message)
    {
        var decodedMessage = GetMessageItems(message);
        var distanceArrayRlp = RlpDecoder.Decode(decodedMessage[1]);
        var distances = new int[distanceArrayRlp.Length];

        for (var i = 0; i < distanceArrayRlp.Length; i++)
            distances[i] = RlpExtensions.ByteArrayToInt32(distanceArrayRlp[i]);

        return new FindNodeMessage(distances)
        {
            RequestId = decodedMessage[0]
        };
    }

    private NodesMessage DecodeNodesMessage(byte[] message)
    {
        var decodedMessage = GetMessageItems(message);
        var requestId = decodedMessage[0];
        var total = RlpExtensions.ByteArrayToInt32(decodedMessage[1]);
        var encodedEnrs = RlpDecoder.Decode(decodedMessage[2]);
        var enrs = enrFactory.CreateFromMultipleEnrList(encodedEnrs, identityManager.Verifier);
        return new NodesMessage(requestId, total, enrs);
    }

    private static TalkReqMessage DecodeTalkReqMessage(byte[] message)
    {
        var decodedMessage = GetMessageItems(message);
        return new TalkReqMessage(decodedMessage[1], decodedMessage[2])
        {
            RequestId = decodedMessage[0]
        };
    }

    private static TalkRespMessage DecodeTalkRespMessage(byte[] message)
    {
        var decodedMessage = GetMessageItems(message);
        return new TalkRespMessage(decodedMessage[0], decodedMessage[1]);
    }

    private RegTopicMessage DecodeRegTopicMessage(byte[] message)
    {
        var decodedMessage = GetMessageItems(message);
        var decodedTopic = decodedMessage[1];
        var enr = enrFactory.CreateFromRlp(decodedMessage[2], identityManager.Verifier);
        var decodedTicket = decodedMessage[3];
        return new RegTopicMessage(decodedTopic, enr, decodedTicket)
        {
            RequestId = decodedMessage[0]
        };
    }

    private static RegConfirmationMessage DecodeRegConfirmationMessage(byte[] message)
    {
        var decodedMessage = GetMessageItems(message);
        return new RegConfirmationMessage(decodedMessage[0], decodedMessage[1]);
    }

    private static TicketMessage DecodeTicketMessage(byte[] message)
    {
        var decodedMessage = GetMessageItems(message);
        return new TicketMessage(decodedMessage[0], decodedMessage[1],
            RlpExtensions.ByteArrayToInt32(decodedMessage[2]));
    }

    private static TopicQueryMessage DecodeTopicQueryMessage(byte[] message)
    {
        var decodedMessage = GetMessageItems(message);
        return new TopicQueryMessage(decodedMessage[1])
        {
            RequestId = decodedMessage[0]
        };
    }
}
