namespace Lantern.Discv5.Enr.Identity;

public interface IIdentityVerifier
{
    bool VerifyRecord(IEnr record);

    byte[] GetNodeIdFromRecord(IEnr record);
}