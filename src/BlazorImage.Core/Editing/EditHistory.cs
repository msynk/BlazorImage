namespace BlazorImage.Editing;

/// <summary>One entry in the edit history.</summary>
public sealed record HistoryEntry(ImageDocument Document, string Label, DateTimeOffset Timestamp)
{
    /// <summary>Rough memory cost of this entry: documents are descriptions, not pixels, so this is small.</summary>
    public int EstimatedSize => 128 + Document.Pipeline.Count * 64 + Document.Annotations.Count * 256;
}

/// <summary>
/// Undo/redo over <see cref="ImageDocument"/> values.
/// </summary>
/// <remarks>
/// Because a document describes the edit rather than holding pixels, one history entry costs a few hundred bytes
/// instead of a full-resolution image. A 4K photo with fifty undo steps needs kilobytes, not gigabytes. The depth limit
/// exists to bound annotation-heavy sessions, not pixel memory.
/// </remarks>
public sealed class EditHistory
{
    private readonly List<HistoryEntry> _entries = [];
    private int _position = -1;

    /// <summary>Creates a history whose initial state is <paramref name="initial"/>.</summary>
    public EditHistory(ImageDocument initial, int maxDepth = 100, string label = "Original")
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        MaxDepth = maxDepth;
        _entries.Add(new HistoryEntry(initial, label, DateTimeOffset.UtcNow));
        _position = 0;
    }

    /// <summary>Maximum number of entries kept. Older entries are discarded first; the oldest is always the original.</summary>
    public int MaxDepth { get; }

    /// <summary>The current document.</summary>
    public ImageDocument Current => _entries[_position].Document;

    /// <summary>The current entry including its label.</summary>
    public HistoryEntry CurrentEntry => _entries[_position];

    /// <summary>All entries, oldest first.</summary>
    public IReadOnlyList<HistoryEntry> Entries => _entries;

    /// <summary>Index of the current entry in <see cref="Entries"/>.</summary>
    public int Position => _position;

    /// <summary>True when <see cref="Undo"/> would change the state.</summary>
    public bool CanUndo => _position > 0;

    /// <summary>True when <see cref="Redo"/> would change the state.</summary>
    public bool CanRedo => _position < _entries.Count - 1;

    /// <summary>Approximate memory held by the history, in bytes.</summary>
    public long EstimatedMemory => _entries.Sum(e => (long)e.EstimatedSize);

    /// <summary>Raised after any change to the current document.</summary>
    public event Action<ImageDocument>? Changed;

    /// <summary>
    /// Records a new state. Anything after the current position is discarded, which is what makes the history a linear
    /// timeline: editing after an undo starts a new branch and forgets the old one.
    /// </summary>
    public void Push(ImageDocument document, string label)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_position < _entries.Count - 1) _entries.RemoveRange(_position + 1, _entries.Count - _position - 1);
        _entries.Add(new HistoryEntry(document, label, DateTimeOffset.UtcNow));
        if (_entries.Count > MaxDepth) _entries.RemoveAt(1); // keep the original at index 0
        _position = _entries.Count - 1;
        Changed?.Invoke(Current);
    }

    /// <summary>
    /// Replaces the current entry instead of adding one. Use while a value is being dragged so a slider does not create
    /// hundreds of undo steps; push once when the interaction ends.
    /// </summary>
    public void Amend(ImageDocument document, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_position == 0)
        {
            // Never overwrite the original state.
            Push(document, label ?? "Edit");
            return;
        }
        _entries[_position] = _entries[_position] with { Document = document, Label = label ?? _entries[_position].Label };
        Changed?.Invoke(Current);
    }

    /// <summary>Steps back one entry. Returns the new current document.</summary>
    public ImageDocument Undo()
    {
        if (CanUndo)
        {
            _position--;
            Changed?.Invoke(Current);
        }
        return Current;
    }

    /// <summary>Steps forward one entry. Returns the new current document.</summary>
    public ImageDocument Redo()
    {
        if (CanRedo)
        {
            _position++;
            Changed?.Invoke(Current);
        }
        return Current;
    }

    /// <summary>Jumps to an arbitrary entry, e.g. from a history panel.</summary>
    public ImageDocument GoTo(int index)
    {
        if (index < 0 || index >= _entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index != _position)
        {
            _position = index;
            Changed?.Invoke(Current);
        }
        return Current;
    }

    /// <summary>Returns to the original state, discarding every edit but keeping the redo trail.</summary>
    public ImageDocument Reset() => GoTo(0);

    /// <summary>Discards all entries and starts again from <paramref name="document"/>.</summary>
    public void Clear(ImageDocument document, string label = "Original")
    {
        ArgumentNullException.ThrowIfNull(document);
        _entries.Clear();
        _entries.Add(new HistoryEntry(document, label, DateTimeOffset.UtcNow));
        _position = 0;
        Changed?.Invoke(Current);
    }
}
