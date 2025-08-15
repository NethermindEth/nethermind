namespace Lantern.Discv5.Enr.Identity;

public interface IIdentitySigner
{
    byte[] PublicKey { get; }

    byte[] SignRecord(IEnr record);
}