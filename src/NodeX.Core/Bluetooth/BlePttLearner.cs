namespace NodeX.Core;

/// <summary>Where the Learn wizard is.</summary>
public enum BleLearnStep
{
    Idle,
    /// <summary>Connecting to the chosen device.</summary>
    Connecting,
    /// <summary>Waiting for the user to press and hold the button.</summary>
    WaitPress,
    /// <summary>Press seen; waiting for the user to let go.</summary>
    WaitRelease,
    Done,
    Failed
}

/// <summary>
/// The Learn wizard's state machine, fed raw characteristic notifications: "press and HOLD the button" records the
/// press packet, "now release" records the release packet. Pure (no Bluetooth, no timers) so it can be tested; the
/// owner of the wizard calls <see cref="TimeOutStep"/> when a step takes too long.
/// </summary>
public sealed class BlePttLearner
{
    private string _address = "";
    private string _name = "";
    private string _pressChar = "";
    private byte[] _press = [];

    public BleLearnStep Step { get; private set; } = BleLearnStep.Idle;

    /// <summary>What to tell the user right now.</summary>
    public string Message { get; private set; } = "";

    /// <summary>Set when <see cref="Step"/> is <see cref="BleLearnStep.Done"/>.</summary>
    public BlePttProfile? Result { get; private set; }

    public bool IsRunning => Step is BleLearnStep.WaitPress or BleLearnStep.WaitRelease;

    public event Action? Changed;

    /// <summary>The connection to the chosen device is being made.</summary>
    public void Connecting()
    {
        Result = null;
        Set(BleLearnStep.Connecting, "Connecting to the button…");
    }

    public void Begin(string address, string name)
    {
        _address = address;
        _name = name;
        _pressChar = "";
        _press = [];
        Result = null;
        Set(BleLearnStep.WaitPress, "Press and HOLD the button on your PTT device.");
    }

    public void Cancel()
    {
        if (Step == BleLearnStep.Idle) return;
        Result = null;
        Set(BleLearnStep.Idle, "");
    }

    /// <summary>One notification from the connected device (the battery characteristic is filtered out by the caller).</summary>
    public void OnNotification(string characteristicUuid, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        switch (Step)
        {
            case BleLearnStep.WaitPress:
                _pressChar = characteristicUuid;
                _press = data.ToArray();
                Set(BleLearnStep.WaitRelease, "Got it. Now RELEASE the button.");
                break;

            case BleLearnStep.WaitRelease:
                // Only the same characteristic counts, and a button that repeats its press packet while held is ignored.
                if (!string.Equals(characteristicUuid, _pressChar, StringComparison.OrdinalIgnoreCase)) return;
                if (data.SequenceEqual(_press)) return;

                Result = new BlePttProfile(_address, _name, _pressChar,
                    BlePttProfile.ToHex(_press), BlePttProfile.ToHex(data));
                Set(BleLearnStep.Done, "Learned. Press the button to test it: the status at the top shows pressed / released. Save to keep it.");
                break;
        }
    }

    /// <summary>The current step took too long.</summary>
    public void TimeOutStep()
    {
        switch (Step)
        {
            case BleLearnStep.WaitPress:
                Result = null;
                Set(BleLearnStep.Failed, "The button sent nothing. Check it is on and connected, then try again.");
                break;

            case BleLearnStep.WaitRelease:
                // Some buttons send a single packet per press and nothing on release. Keep the press: it then acts
                // as a tap (key, or flip in Toggle mode) and can never leave the transmitter stuck on.
                Result = new BlePttProfile(_address, _name, _pressChar, BlePttProfile.ToHex(_press), "");
                Set(BleLearnStep.Done, "This button only sends a press, no release. It will work as a tap: turn on Toggle PTT so a tap starts and a second tap stops. Save to keep it.");
                break;
        }
    }

    public void Fail(string message)
    {
        Result = null;
        Set(BleLearnStep.Failed, message);
    }

    private void Set(BleLearnStep step, string message)
    {
        Step = step;
        Message = message;
        Changed?.Invoke();
    }
}
