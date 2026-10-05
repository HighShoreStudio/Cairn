using System.Windows.Input;
using HighshoreCairn.Models;

namespace HighshoreCairn.ViewModels;

public enum PomodoroPhase { Focus, ShortBreak, LongBreak }

/// <summary>
/// The "tomato timer" shown in the header of a project: focus sessions separated by short breaks,
/// with a long break every N sessions. Durations, sounds and visibility come from the project settings.
/// </summary>
public class PomodoroViewModel : ObservableObject, IDisposable
{
    private readonly ISoundService? _sound;
    private readonly Func<DateTime> _clock;
    private readonly SynchronizationContext? _context;
    private System.Timers.Timer? _timer;
    private PomodoroSettings _settings;
    private DateTime _endsAt;
    private TimeSpan _remaining;

    public PomodoroViewModel(PomodoroSettings settings, ISoundService? sound = null, Func<DateTime>? clock = null)
    {
        _settings = settings;
        _sound = sound;
        _clock = clock ?? (() => DateTime.UtcNow);
        _remaining = DurationOf(PomodoroPhase.Focus);
        ToggleCommand = new RelayCommand(Toggle);
        ResetCommand = new RelayCommand(Reset);
        SkipCommand = new RelayCommand(Skip);

        // On the UI thread the timer ticks by itself; without a UI (tests) Tick() is called by hand.
        _context = SynchronizationContext.Current;
        if (_context != null)
        {
            _timer = new System.Timers.Timer(250) { AutoReset = true };
            _timer.Elapsed += (_, _) => _context.Post(_ => Tick(), null);
        }
    }

    public ICommand ToggleCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand SkipCommand { get; }

    /// <summary>Raised when a phase ends, with a short message for the status bar.</summary>
    public event Action<string>? Finished;

    private PomodoroPhase _phase = PomodoroPhase.Focus;
    public PomodoroPhase Phase
    {
        get => _phase;
        private set
        {
            if (!SetProperty(ref _phase, value)) return;
            OnPropertyChanged(nameof(PhaseLabel));
            OnPropertyChanged(nameof(IsBreak));
            OnPropertyChanged(nameof(IsFocus));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    public string PhaseLabel => _phase switch
    {
        PomodoroPhase.ShortBreak => "Short break",
        PomodoroPhase.LongBreak => "Long break",
        _ => "Focus"
    };

    public bool IsFocus => _phase == PomodoroPhase.Focus;
    public bool IsBreak => _phase != PomodoroPhase.Focus;

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(ToggleToolTip));
            if (_timer != null) _timer.Enabled = value;
        }
    }

    public bool IsPaused => !_isRunning;
    public string ToggleToolTip => _isRunning ? "Pause the timer" : "Start the timer";

    /// <summary>Time left in the current phase.</summary>
    public TimeSpan Remaining
    {
        get => _remaining;
        private set
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            if (_remaining == value) return;
            var before = TimeText;
            _remaining = value;
            if (TimeText != before) OnPropertyChanged(nameof(TimeText));
            OnPropertyChanged(nameof(Progress));
        }
    }

    /// <summary>"24:59" (minutes can exceed 59 for long sessions).</summary>
    public string TimeText
    {
        get
        {
            var seconds = (int)Math.Ceiling(_remaining.TotalSeconds);
            return $"{seconds / 60:00}:{seconds % 60:00}";
        }
    }

    /// <summary>0 at the start of the phase, 1 at its end.</summary>
    public double Progress
    {
        get
        {
            var total = DurationOf(_phase).TotalSeconds;
            return total <= 0 ? 0 : Math.Clamp(1 - _remaining.TotalSeconds / total, 0, 1);
        }
    }

    private int _completedSessions;
    /// <summary>Focus sessions completed since the project was opened.</summary>
    public int CompletedSessions
    {
        get => _completedSessions;
        private set
        {
            if (!SetProperty(ref _completedSessions, value)) return;
            OnPropertyChanged(nameof(SessionText));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    /// <summary>Position inside the cycle, e.g. "2/4" = second session of four before the long break.</summary>
    public string SessionText
    {
        get
        {
            var every = Math.Max(1, _settings.LongBreakEvery);
            var done = _completedSessions % every;
            var current = _phase == PomodoroPhase.Focus ? done + 1 : done == 0 ? every : done;
            return $"{current}/{every}";
        }
    }

    public string ToolTip =>
        $"Tomato timer · {PhaseLabel}\n" +
        $"Session {SessionText} · {_completedSessions} completed\n" +
        "Durations and sounds: Settings > Timer";

    /// <summary>False when the timer is hidden in the project settings.</summary>
    public bool IsVisible => !_settings.Hidden;

    public TimeSpan DurationOf(PomodoroPhase phase) => TimeSpan.FromMinutes(Math.Max(1, phase switch
    {
        PomodoroPhase.ShortBreak => _settings.ShortBreakMinutes,
        PomodoroPhase.LongBreak => _settings.LongBreakMinutes,
        _ => _settings.FocusMinutes
    }));

    /// <summary>Called after the project settings were saved.</summary>
    public void ApplySettings(PomodoroSettings settings)
    {
        var untouched = !_isRunning && _remaining == DurationOf(_phase);
        _settings = settings;
        if (untouched) Remaining = DurationOf(_phase);
        else if (_remaining > DurationOf(_phase)) Remaining = DurationOf(_phase);
        if (_isRunning) _endsAt = _clock() + _remaining;
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(SessionText));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ToolTip));
        if (settings.Hidden && _isRunning) IsRunning = false; // a hidden timer never rings by surprise
    }

    public void Start()
    {
        if (_isRunning) return;
        if (_remaining <= TimeSpan.Zero) Remaining = DurationOf(_phase);
        _endsAt = _clock() + _remaining;
        IsRunning = true;
    }

    public void Pause()
    {
        if (!_isRunning) return;
        Remaining = _endsAt - _clock();
        IsRunning = false;
    }

    private void Toggle()
    {
        if (_isRunning) Pause();
        else Start();
    }

    /// <summary>Back to the start of the current phase, stopped.</summary>
    public void Reset()
    {
        IsRunning = false;
        Remaining = DurationOf(_phase);
    }

    /// <summary>Jumps to the next phase without counting the current one as completed.</summary>
    public void Skip()
    {
        var wasRunning = _isRunning;
        IsRunning = false;
        SetPhase(_phase == PomodoroPhase.Focus ? NextBreak(_completedSessions + 1) : PomodoroPhase.Focus);
        if (wasRunning) Start();
    }

    private PomodoroPhase NextBreak(int sessionsDone) =>
        sessionsDone % Math.Max(1, _settings.LongBreakEvery) == 0 ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak;

    private void SetPhase(PomodoroPhase phase)
    {
        Phase = phase;
        _remaining = TimeSpan.MinValue; // force the notifications
        Remaining = DurationOf(phase);
        OnPropertyChanged(nameof(SessionText));
    }

    /// <summary>Updates the countdown. Called by the internal timer (or directly by the tests).</summary>
    public void Tick()
    {
        if (!_isRunning) return;
        Remaining = _endsAt - _clock();
        if (_remaining <= TimeSpan.Zero) Complete();
    }

    private void Complete()
    {
        IsRunning = false;
        try { _sound?.Play(_settings.Sound, _settings.Volume, _settings.CustomSoundFile); }
        catch { /* a sound that cannot be played must never stop the timer */ }

        string message;
        bool autoStart;
        if (_phase == PomodoroPhase.Focus)
        {
            CompletedSessions++;
            var next = NextBreak(_completedSessions);
            SetPhase(next);
            autoStart = _settings.AutoStartBreaks;
            message = next == PomodoroPhase.LongBreak
                ? "Focus session completed: time for a long break."
                : "Focus session completed: time for a short break.";
        }
        else
        {
            SetPhase(PomodoroPhase.Focus);
            autoStart = _settings.AutoStartFocus;
            message = "Break finished: back to focus.";
        }

        if (autoStart) Start();
        Finished?.Invoke(message);
    }

    public void Dispose()
    {
        _isRunning = false;
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }
}
