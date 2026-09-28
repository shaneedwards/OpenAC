using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Chat;

/// <summary>
/// The ordered set of predicates a line is offered as it is appended. Used
/// both for <see cref="ChatLog.Filters"/>, where a rejection drops the line
/// everywhere, and <see cref="ChatLog.DisplayFilters"/>, where it only keeps
/// the line off the display.
/// </summary>
public sealed class ChatSuppressionFilters
{
    private readonly object _gate = new();
    private Func<PluginChatMessage, bool>[] _filters = [];

    /// <summary>
    /// Reports a filter that threw. A throwing filter suppresses nothing, but
    /// it should not do so silently.
    /// </summary>
    public Action<Exception>? FilterFaulted { get; set; }

    public int Count
    {
        get
        {
            lock (_gate)
                return _filters.Length;
        }
    }

    /// <summary>
    /// Adds a filter at the end of the order. Dispose the result to remove it.
    /// </summary>
    public IDisposable Register(Func<PluginChatMessage, bool> suppress)
    {
        ArgumentNullException.ThrowIfNull(suppress);
        lock (_gate)
        {
            var replacement = new Func<PluginChatMessage, bool>[_filters.Length + 1];
            Array.Copy(_filters, replacement, _filters.Length);
            replacement[^1] = suppress;
            _filters = replacement;
        }
        return new Registration(this, suppress);
    }

    /// <summary>
    /// True when any filter rejects the candidate. Filters run in registration
    /// order and stop at the first rejection.
    /// </summary>
    public bool ShouldSuppress(in PluginChatMessage candidate)
    {
        Func<PluginChatMessage, bool>[] filters;
        lock (_gate)
            filters = _filters;
        if (filters.Length == 0)
            return false;

        foreach (Func<PluginChatMessage, bool> filter in filters)
        {
            try
            {
                if (filter(candidate))
                    return true;
            }
            catch (Exception error)
            {
                FilterFaulted?.Invoke(error);
            }
        }
        return false;
    }

    private void Remove(Func<PluginChatMessage, bool> suppress)
    {
        lock (_gate)
        {
            int index = Array.IndexOf(_filters, suppress);
            if (index < 0)
                return;
            if (_filters.Length == 1)
            {
                _filters = [];
                return;
            }
            var replacement = new Func<PluginChatMessage, bool>[_filters.Length - 1];
            if (index > 0)
                Array.Copy(_filters, 0, replacement, 0, index);
            if (index < _filters.Length - 1)
            {
                Array.Copy(
                    _filters,
                    index + 1,
                    replacement,
                    index,
                    _filters.Length - index - 1);
            }
            _filters = replacement;
        }
    }

    private sealed class Registration(
        ChatSuppressionFilters owner,
        Func<PluginChatMessage, bool> suppress) : IDisposable
    {
        private ChatSuppressionFilters? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Remove(suppress);
    }
}
