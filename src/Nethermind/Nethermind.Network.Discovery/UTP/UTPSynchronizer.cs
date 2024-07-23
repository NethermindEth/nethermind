// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

public class UTPSynchronizer
{
    private TaskCompletionSource _messageTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<UTPPacketHeader> _syncTcs = new();

    const int WaitForMessageDelayMs = 10;

    public async Task<UTPPacketHeader> WaitTillSenderSendsST_SYNAndReceiverReceiveIt()
    {
        return await _syncTcs.Task;
    }


    public async Task<bool> WaitForReceiverToSync()
    {
        var delay = Task.Delay(WaitForMessageDelayMs);
        if (await Task.WhenAny(_messageTcs.Task, delay) == delay)
        {
            return false;
        }

        _messageTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return true;
    }


    //This should awake the readStream method on the receiver to move forward with the sync
    public void AwakeReceiverToStarSynchronization(UTPPacketHeader meta)
    {
        _syncTcs.TrySetResult(meta);
    }

    public void awakePeer()
    {
        _messageTcs.TrySetResult();
    }
}
