namespace Jane.Core.History;

/// <param name="Text">The text as it stood before the edit that produced the next entry.</param>
public sealed record UndoEntry(string Text, DateTimeOffset At, string Description);

/// <summary>
/// A bounded history of in-place edits to one running piece of text, so a spoken "undo that" can
/// walk back and "go back to the original" can return all the way.
/// </summary>
/// <remarks>
/// <para>
/// A *chain*, not a per-selection stack. Each edit replaces the selection, so the next edit's
/// selection is the previous edit's output -- keying on the selection text alone would start a
/// fresh stack on every step and multi-step undo would never work at all. The stack therefore
/// tracks what it last produced, and an edit whose "before" matches that continues the chain;
/// anything else is a different piece of text and starts over. Undoing into a different piece of
/// text is not undo, it is corruption.
/// </para>
/// <para>
/// In memory only, and cleared on focus change and on restart. That is a deliberate limit: a
/// persisted stack would let "go back to the original" restore text into a document that has since
/// been edited by hand, which is worse than not offering the command.
/// </para>
/// <para>
/// Bounded, because Edit Mode invites repeated tries -- "make it shorter", "shorter", "no, formal"
/// -- and an unbounded stack of document-sized strings is a memory leak with a friendly name.
/// </para>
/// </remarks>
public sealed class UndoStack(int capacity = UndoStack.DefaultCapacity)
{
    public const int DefaultCapacity = 20;

    private readonly List<UndoEntry> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>What the most recent edit produced. The next edit must start from this to chain.</summary>
    private string? _currentText;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool CanUndo => Count > 0;

    /// <summary>The text this stack would restore to if asked to go all the way back.</summary>
    public string? Original
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count == 0 ? null : _entries[0].Text;
            }
        }
    }

    /// <summary>
    /// Records one edit: the text before it, and the text it produced.
    /// </summary>
    /// <remarks>
    /// Both halves are needed. The "before" is what an undo restores; the "after" is what the
    /// stack watches for to know whether the next edit is a continuation or a new chain.
    /// </remarks>
    public void Push(string textBeforeEdit, string textAfterEdit, string description)
    {
        lock (_gate)
        {
            var continues = _entries.Count > 0
                            && _currentText is not null
                            && string.Equals(textBeforeEdit, _currentText, StringComparison.Ordinal);

            if (!continues)
            {
                _entries.Clear();
            }

            _entries.Add(new UndoEntry(textBeforeEdit, DateTimeOffset.Now, description));
            _currentText = textAfterEdit;

            // Drop from the bottom, which loses the true original on a very long chain. The
            // alternative -- refusing further edits at capacity -- would be worse: the user would
            // simply be unable to keep refining.
            while (_entries.Count > capacity)
            {
                _entries.RemoveAt(0);
            }
        }
    }

    /// <summary>Pops one step. "Undo that", "go back one step".</summary>
    public UndoEntry? Pop()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return null;
            }

            var entry = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);

            // The restored text becomes the current text, so an immediate second undo chains.
            _currentText = entry.Text;
            return entry;
        }
    }

    /// <summary>
    /// Restores the first recorded state and empties the stack. "Go back to the original".
    /// </summary>
    public UndoEntry? PopToOriginal()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return null;
            }

            var original = _entries[0];
            _entries.Clear();
            _currentText = original.Text;
            return original;
        }
    }

    /// <summary>Called on focus change: an undo aimed at a window the user has left is nonsense.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _currentText = null;
        }
    }
}
