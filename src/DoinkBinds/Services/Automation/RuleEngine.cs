using System.Collections.Concurrent;
using DoinkBinds.Models;

namespace DoinkBinds.Services.Automation;

/// <summary>Live state of a rule for the UI.</summary>
public sealed record RuleStatus(Guid RuleId, bool? LastResult, DateTime? LastCheckedUtc, DateTime? LastFiredUtc,
    int FireCount, string? Detail, double LastCheckMs);

/// <summary>
/// Background "IF condition THEN steps" watcher. Each enabled rule is checked every
/// <see cref="Rule.IntervalMs"/>; when it fires, its steps are played through the normal player
/// (so everything a macro can do, a rule can do — and Stop / F9 stops it too).
/// </summary>
public interface IRuleEngine
{
    bool IsRunning { get; }
    void Start();
    void Stop();

    IReadOnlyDictionary<Guid, RuleStatus> Statuses { get; }
    event EventHandler<RuleStatus>? StatusChanged;

    /// <summary>Raised when a rule fires (or is skipped because something else is playing).</summary>
    event EventHandler<(Rule Rule, bool Started)>? RuleFired;

    /// <summary>Evaluates a rule's condition once (for the "Test now" button).</summary>
    ConditionResult Test(Rule rule);

    /// <summary>Runs a rule's steps immediately.</summary>
    bool RunNow(Rule rule, DesktopPoint? found = null);

    /// <summary>Lets the host pause rules (e.g. while recording or picking on screen).</summary>
    Func<bool>? IsSuspended { get; set; }
}

public sealed class RuleEngine : IRuleEngine, IDisposable
{
    private const int TickMs = 50;

    private readonly IAutomationStore _store;
    private readonly IScreenVision _vision;
    private readonly IMacroPlayerService _player;
    private readonly VariableStore _variables;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    private readonly ConcurrentDictionary<Guid, RuleStatus> _statuses = new();
    private readonly Dictionary<Guid, (bool Prev, DateTime NextDue, DateTime LastFired, int Fires)> _state = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    public RuleEngine(IAutomationStore store, IScreenVision vision, IMacroPlayerService player, VariableStore variables,
        ISettingsService settings, ILogService log)
    {
        _store = store;
        _vision = vision;
        _player = player;
        _variables = variables;
        _settings = settings;
        _log = log;
    }

    public bool IsRunning => _thread is { IsAlive: true };
    public IReadOnlyDictionary<Guid, RuleStatus> Statuses => _statuses;
    public event EventHandler<RuleStatus>? StatusChanged;
    public event EventHandler<(Rule Rule, bool Started)>? RuleFired;
    public Func<bool>? IsSuspended { get; set; }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        lock (_state) _state.Clear();
        _thread = new Thread(() => Loop(ct)) { IsBackground = true, Name = "DoinkBinds.Rules", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Tick(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _log.Error("Rule engine tick failed", ex);
            }
            ct.WaitHandle.WaitOne(TickMs);
        }
    }

    /// <summary>One scheduler pass. Public for tests.</summary>
    public void Tick(DateTime now)
    {
        if (!_settings.Settings.RulesEnabled || !_settings.Settings.MacrosEnabled) return;
        if (IsSuspended?.Invoke() == true) return;

        List<Rule> rules;
        lock (_store.Data) rules = _store.Data.Rules.Where(r => r.Enabled).ToList();

        foreach (var rule in rules)
        {
            (bool Prev, DateTime NextDue, DateTime LastFired, int Fires) st;
            lock (_state)
            {
                if (!_state.TryGetValue(rule.Id, out st)) st = (false, DateTime.MinValue, DateTime.MinValue, 0);
            }
            if (now < st.NextDue) continue;
            if (rule.MaxFires > 0 && st.Fires >= rule.MaxFires) continue;

            var started = DateTime.UtcNow;
            var result = Evaluate(rule);
            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            st.NextDue = now.AddMilliseconds(Math.Max(TickMs, rule.IntervalMs));

            var shouldFire = result.Met && (rule.Trigger == RuleTriggerMode.WhileTrue || !st.Prev)
                             && (now - st.LastFired).TotalMilliseconds >= rule.CooldownMs;
            var prev = result.Met;

            if (shouldFire)
            {
                if (_player.State != AppStatus.Idle)
                {
                    if (!rule.SkipWhileBusy) prev = false; // try again next time (waits until idle)
                    RuleFired?.Invoke(this, (rule, false));
                }
                else if (RunNow(rule, result.Found))
                {
                    st.LastFired = now;
                    st.Fires++;
                    RuleFired?.Invoke(this, (rule, true));
                }
                else
                {
                    prev = false;
                }
            }
            st.Prev = prev;
            lock (_state) _state[rule.Id] = st;

            var status = new RuleStatus(rule.Id, result.Met, DateTime.UtcNow,
                st.LastFired == DateTime.MinValue ? null : st.LastFired, st.Fires, result.Detail, elapsed);
            _statuses[rule.Id] = status;
            StatusChanged?.Invoke(this, status);
        }
    }

    private ConditionResult Evaluate(Rule rule) =>
        _vision.Evaluate(rule.Condition, new VisionContext { GetVariable = _variables.Get });

    public ConditionResult Test(Rule rule) => Evaluate(rule);

    public bool RunNow(Rule rule, DesktopPoint? found = null)
    {
        if (rule.Actions.Count == 0) return false;
        var macro = new Macro { Id = rule.Id, Name = $"Rule: {rule.Name}", Actions = rule.Actions };
        return _player.Play(macro, new PlaybackSettings { Mode = PlaybackMode.Once, RestoreWindowFocus = false, LoopDelayMs = 0 }, found);
    }

    public void Dispose() => Stop();
}
