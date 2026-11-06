namespace NodeX.Core;

/// <summary>
/// Maps one physical phone button (a volume key, a headset button...) to push-to-talk. A platform head feeds every
/// key event to <see cref="OnKey"/>; the mapped key is consumed and reported through <see cref="Pressed"/>, and
/// "learn" mode captures whichever key the user presses next so they can pick the button themselves.
/// Key codes are whatever the platform uses (Android <c>KeyEvent</c> codes); 0 means "none".
/// </summary>
public sealed class HardwarePttMapper
{
    // Android codes that must never be captured or consumed: Home, Back, Power, App switch, Menu.
    private static readonly HashSet<int> Reserved = [3, 4, 26, 82, 187];

    private readonly Lock _gate = new();
    private int _mapped;
    private bool _learning;
    private int _swallowUpFor;
    private bool _isDown;

    /// <summary>The key that acts as PTT; 0 = none.</summary>
    public int MappedKey
    {
        get { lock (_gate) return _mapped; }
        set
        {
            bool release;
            lock (_gate)
            {
                if (Reserved.Contains(value)) value = 0;
                if (_mapped == value) return;
                release = _isDown;
                _mapped = value;
                _isDown = false;
            }
            if (release) Pressed?.Invoke(false);   // never leave a key "held" when the mapping changes
        }
    }

    public bool IsLearning
    {
        get { lock (_gate) return _learning; }
    }

    /// <summary>Raised with true when the mapped key goes down and false when it comes up (not for auto-repeat).</summary>
    public event Action<bool>? Pressed;

    /// <summary>Raised when learn mode captured a key.</summary>
    public event Action<int>? KeyLearned;

    /// <summary>Raised when learn mode starts or ends without a key (cancelled).</summary>
    public event Action? LearningChanged;

    /// <summary>The next ordinary key press is captured instead of being delivered.</summary>
    public void BeginLearn()
    {
        lock (_gate) _learning = true;
        LearningChanged?.Invoke();
    }

    public void CancelLearn()
    {
        bool was;
        lock (_gate) { was = _learning; _learning = false; }
        if (was) LearningChanged?.Invoke();
    }

    /// <summary>
    /// Offers a key event to the mapper. Returns true when it was consumed (the caller must not pass it on).
    /// </summary>
    /// <param name="isRepeat">True for auto-repeat while the key is held.</param>
    public bool OnKey(int keyCode, bool down, bool isRepeat)
    {
        if (Reserved.Contains(keyCode)) return false;

        bool learned = false;
        bool? press = null;
        bool consume;

        lock (_gate)
        {
            if (_learning && down && !isRepeat)
            {
                _learning = false;
                _swallowUpFor = keyCode;
                learned = true;
                consume = true;
            }
            else if (!down && keyCode == _swallowUpFor && _swallowUpFor != 0)
            {
                _swallowUpFor = 0;
                consume = true;               // the release of the key we just learned
            }
            else if (_mapped != 0 && keyCode == _mapped)
            {
                consume = true;
                if (!isRepeat)
                {
                    if (down && !_isDown) { _isDown = true; press = true; }
                    else if (!down && _isDown) { _isDown = false; press = false; }
                }
            }
            else
            {
                consume = false;
            }
        }

        if (learned)
        {
            KeyLearned?.Invoke(keyCode);
            LearningChanged?.Invoke();
        }
        if (press is { } p) Pressed?.Invoke(p);
        return consume;
    }
}
