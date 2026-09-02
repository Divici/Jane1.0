using System.Collections.ObjectModel;
using Jane.App.Controls;
using Jane.Core.Instructions;

namespace Jane.App.Settings;

/// <summary>
/// Custom Instructions: one global block of free-text style rules, plus a table of per-app
/// overrides keyed on process name.
/// </summary>
/// <remarks>
/// <para>
/// The per-app table is Jane's deliberate improvement on Aqua, which offers a single instructions
/// box and asks the model to work out which application it is in. A table keyed on the process
/// name makes "terse in the terminal, formal in Outlook" a fact rather than a hint -- and it is
/// what lets the bypass know, before any model runs, that this app has rules and must not be
/// skipped.
/// </para>
/// <para>
/// Global and per-app rules compose rather than replace, and the window says so: somebody who
/// wrote "never use exclamation marks" meant it in Slack too.
/// </para>
/// </remarks>
public sealed class InstructionsEditor : ObservableObject
{
    private readonly CustomInstructions _instructions;
    private string _globalText = string.Empty;
    private string _draftProcess = string.Empty;
    private string _draftText = string.Empty;
    private string? _error;
    private bool _globalDirty;

    public InstructionsEditor(CustomInstructions instructions)
    {
        _instructions = instructions;
        _instructions.Changed += OnChanged;
        Refresh();
    }

    /// <summary>Per-app overrides only. The global block has its own text area above them.</summary>
    public ObservableCollection<InstructionSet> AppRules { get; } = [];

    /// <summary>
    /// The global rules. Applies in every application, which is why a non-empty value suppresses
    /// the bypass everywhere.
    /// </summary>
    public string GlobalText
    {
        get => _globalText;
        set
        {
            if (Set(ref _globalText, value))
            {
                GlobalDirty = true;
            }
        }
    }

    /// <summary>Whether the text area holds unsaved edits. Drives the Save button's enabled state.</summary>
    public bool GlobalDirty
    {
        get => _globalDirty;
        private set => Set(ref _globalDirty, value);
    }

    /// <summary>A process name as the focus tracker reports it. "Code.exe" and "code" are the same app.</summary>
    public string DraftProcess
    {
        get => _draftProcess;
        set
        {
            if (Set(ref _draftProcess, value))
            {
                Raise(nameof(CanAddAppRule));
            }
        }
    }

    public string DraftText
    {
        get => _draftText;
        set
        {
            if (Set(ref _draftText, value))
            {
                Raise(nameof(CanAddAppRule));
            }
        }
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool CanAddAppRule =>
        !string.IsNullOrWhiteSpace(DraftProcess) && !string.IsNullOrWhiteSpace(DraftText);

    /// <summary>True when nothing at all is configured, so the pane can say so rather than look broken.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(GlobalText) && AppRules.Count == 0;

    public async Task SaveGlobalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _instructions.SetGlobalAsync(GlobalText, cancellationToken);
            GlobalDirty = false;
            Error = null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Error = "Could not save the global instructions: " + ex.Message;
        }
    }

    public async Task AddAppRuleAsync(CancellationToken cancellationToken)
    {
        if (!CanAddAppRule)
        {
            return;
        }

        try
        {
            await _instructions.SetForAppAsync(DraftProcess, DraftText, cancellationToken);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Error = $"Could not save the rules for {DraftProcess}: {ex.Message}";
            return;
        }

        DraftProcess = string.Empty;
        DraftText = string.Empty;
        Error = null;
    }

    /// <summary>Loads a rule back into the form. Saving again overwrites it, since the key is the app.</summary>
    public void Edit(InstructionSet rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        DraftProcess = rule.ProcessName ?? string.Empty;
        DraftText = rule.Text;
    }

    public Task SetEnabledAsync(InstructionSet rule, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return _instructions.SetEnabledAsync(rule.Id, enabled, cancellationToken);
    }

    public async Task RemoveAsync(InstructionSet rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await _instructions.RemoveAsync(rule.Id, cancellationToken);
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        AppRules.Clear();
        foreach (var rule in _instructions.All.Where(r => !r.IsGlobal))
        {
            AppRules.Add(rule);
        }

        // Only adopt the stored text when the user is not mid-edit; a Changed event raised by
        // saving an app rule must not discard an unsaved global block.
        if (!GlobalDirty)
        {
            var stored = _instructions.All.FirstOrDefault(r => r.IsGlobal)?.Text ?? string.Empty;
            if (Set(ref _globalText, stored, nameof(GlobalText)))
            {
                GlobalDirty = false;
            }
        }

        Raise(nameof(IsEmpty));
    }

    public void Detach() => _instructions.Changed -= OnChanged;
}
