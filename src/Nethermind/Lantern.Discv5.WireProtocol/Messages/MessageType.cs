namespace Lantern.Discv5.WireProtocol.Messages;

public enum MessageType : byte
{
    Ping = 0x01,
    Pong = 0x02,
    FindNode = 0x03,
    Nodes = 0x04,
    TalkReq = 0x05,
    TalkResp = 0x06,
    RegTopic = 0x07,
    Ticket = 0x08,
    RegConfirmation = 0x09,
    TopicQuery = 0x0A
}