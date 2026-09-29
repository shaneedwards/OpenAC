using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Chat;

/// <summary>
/// The ordered set of rewrites a line is offered as it is appended, for
/// <see cref="ChatLog.DisplayRewrites"/>. The first to return non-null
/// replaces what follows the sender on the display; every reader downstream
/// of the log still gets the line as it arrived.
/// </summary>
public sealed class ChatDisplayRewrites
{
    private readonly object _gate = new();
    private Func<PluginChatMessage, string?>[] _rewrites = [];

    /// <summary>
    /// Reports a rewrite that threw. A throwing rewrite changes nothing, but
    /// it should not do so silently.
    /// </summary>
    public Action<Exception>? RewriteFaulted { get; set; }

    public int Count
    {
        get
        {
            lock (_gate)
                return _rewrites.Length;
        }
    }

    /// <summary>
    /// Adds a rewrite at the end of the order. Dispose the result to remove it.
    /// </summary>
    public IDisposable Register(Func<PluginChatMessage, string?> rewrite)
    {
        ArgumentNullException.ThrowIfNull(rewrite);
        lock (_gate)
        {
            var replacement = new Func<PluginChatMessage, string?>[_rewrites.Length + 1];
            Array.Copy(_rewrites, replacement, _rewrites.Length);
            replacement[^1] = rewrite;
            _rewrites = replacement;
        }
        return new Registration(this, rewrite);
    }

    /// <summary>
    /// The first non-null answer, or null when none matched. Rewrites run in
    /// registration order, and one that throws is skipped.
    /// </summary>
    public string? Rewrite(in PluginChatMessage candidate)
    {
        Func<PluginChatMessage, string?>[] rewrites;
        lock (_gate)
            rewrites = _rewrites;

        foreach (Func<PluginChatMessage, string?> rewrite in rewrites)
        {
            try
            {
                if (rewrite(candidate) is { } text)
                    return text;
            }
            catch (Exception error)
            {
                RewriteFaulted?.Invoke(error);
            }
        }
        return null;
    }

    private void Remove(Func<PluginChatMessage, string?> rewrite)
    {
        lock (_gate)
        {
            int index = Array.IndexOf(_rewrites, rewrite);
            if (index < 0)
                return;
            if (_rewrites.Length == 1)
            {
                _rewrites = [];
                return;
            }
            var replacement = new Func<PluginChatMessage, string?>[_rewrites.Length - 1];
            if (index > 0)
                Array.Copy(_rewrites, 0, replacement, 0, index);
            if (index < _rewrites.Length - 1)
            {
                Array.Copy(
                    _rewrites,
                    index + 1,
                    replacement,
                    index,
                    _rewrites.Length - index - 1);
            }
            _rewrites = replacement;
        }
    }

    private sealed class Registration(
        ChatDisplayRewrites owner,
        Func<PluginChatMessage, string?> rewrite) : IDisposable
    {
        private ChatDisplayRewrites? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Remove(rewrite);
    }
}
