using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity;

namespace Lantern.Discv5.WireProtocol.Table;

public class NodeTableEntry(IEnr record, IIdentityVerifier verifier)
{
    public byte[] Id { get; } = verifier.GetNodeIdFromRecord(record);

    public IEnr Record { get; set; } = record;

    public NodeStatus Status { get; set; } = NodeStatus.None;

    public int FailureCounter { get; set; }

    public bool HasRespondedEver { get; set; }

    public DateTime LastSeen { get; set; }
}