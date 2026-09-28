using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

public sealed class ChatDisplayFilterTests
{
    [Fact]
    public void AHiddenLineNeverEntersTheTranscriptOrBumpsTheRevision()
    {
        var log = new ChatLog();
        using IDisposable hide = log.DisplayFilters.Register(
            static candidate => candidate.Kind == (int)ChatKind.Tell);
        long revisionBefore = log.Revision;

        log.OnTellReceived("Bob", "psst", 0x50000001u, logTextType: 0x03u);

        Assert.Empty(log.Snapshot());
        Assert.Equal(0, log.Count);
        Assert.Equal(revisionBefore, log.Revision);
    }

    [Fact]
    public void AHiddenLineStillFiresEntryAppendedFlaggedWithASequence()
    {
        var log = new ChatLog();
        using IDisposable hide = log.DisplayFilters.Register(static _ => true);
        ChatEntry? seen = null;
        log.EntryAppended += entry => seen = entry;

        log.OnSystemMessage("hidden", 0u);

        Assert.NotNull(seen);
        Assert.True(seen!.Value.HiddenFromDisplay);
        Assert.NotEqual(0L, seen.Value.Sequence);
    }

    [Fact]
    public void ASuppressionFilterWinsOverADisplayFilter()
    {
        var log = new ChatLog();
        using IDisposable suppress = log.Filters.Register(static _ => true);
        int offered = 0;
        using IDisposable hide = log.DisplayFilters.Register(_ => { offered++; return true; });
        int appended = 0;
        log.EntryAppended += _ => appended++;

        log.OnSystemMessage("dropped", 0u);

        Assert.Equal(0, appended);
        Assert.Equal(0, offered);
    }

    [Fact]
    public void DisplayFiltersSeeTheUncensoredLine()
    {
        var log = new ChatLog
        {
            FilterLanguageSource = () => true,
            FilterLanguagePatterns = new[] { "zork" },
        };
        string? seenText = null;
        using IDisposable hide = log.DisplayFilters.Register(candidate =>
        {
            seenText = candidate.Text;
            return false;
        });

        log.OnSystemMessage("a zork b", 0u);

        Assert.Equal("a zork b", seenText);
        Assert.Equal("a **** b", log.Snapshot()[0].Text);
    }

    [Fact]
    public void AThrowingDisplayFilterHidesNothing()
    {
        var log = new ChatLog();
        using IDisposable faulty = log.DisplayFilters.Register(
            static _ => throw new InvalidOperationException("boom"));

        log.OnSystemMessage("kept", 0u);

        ChatEntry entry = Assert.Single(log.Snapshot());
        Assert.False(entry.HiddenFromDisplay);
    }

    [Fact]
    public void APluginsOwnRepostIsOfferedToDisplayFilters()
    {
        var log = new ChatLog();
        var offered = new List<string>();
        using IDisposable hide = log.DisplayFilters.Register(candidate =>
        {
            offered.Add(candidate.Text);
            return false;
        });

        log.OnSelfSent(ChatKind.Tell, "hey", logTextType: 0x04u, targetOrChannel: "Alice");

        Assert.Equal(["hey"], offered);
    }
}
