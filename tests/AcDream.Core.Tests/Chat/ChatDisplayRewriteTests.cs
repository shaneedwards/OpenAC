using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

public sealed class ChatDisplayRewriteTests
{
    [Fact]
    public void TheFirstNonNullRewriteWins()
    {
        var log = new ChatLog();
        using IDisposable pass = log.DisplayRewrites.Register(static _ => null);
        using IDisposable first = log.DisplayRewrites.Register(static _ => "first");
        using IDisposable second = log.DisplayRewrites.Register(static _ => "second");

        log.OnSystemMessage("hello", 0u);

        Assert.Equal("first", log.Snapshot()[0].DisplayRewrite);
    }

    [Fact]
    public void AThrowingRewriteChangesNothing()
    {
        var log = new ChatLog();
        using IDisposable faulty = log.DisplayRewrites.Register(
            static _ => throw new InvalidOperationException("boom"));
        using IDisposable next = log.DisplayRewrites.Register(static _ => "kept");

        log.OnSystemMessage("hello", 0u);

        Assert.Equal("kept", log.Snapshot()[0].DisplayRewrite);
    }

    [Fact]
    public void AHiddenLineIsNotOfferedToRewrites()
    {
        var log = new ChatLog();
        using IDisposable hide = log.DisplayFilters.Register(static _ => true);
        int offered = 0;
        using IDisposable rewrite = log.DisplayRewrites.Register(_ => { offered++; return "x"; });
        ChatEntry? seen = null;
        log.EntryAppended += entry => seen = entry;

        log.OnSystemMessage("hidden", 0u);

        Assert.Equal(0, offered);
        Assert.Null(seen!.Value.DisplayRewrite);
    }

    [Fact]
    public void RewritesSeeTheUncensoredLine()
    {
        var log = new ChatLog
        {
            FilterLanguageSource = () => true,
            FilterLanguagePatterns = new[] { "zork" },
        };
        string? seenText = null;
        using IDisposable rewrite = log.DisplayRewrites.Register(candidate =>
        {
            seenText = candidate.Text;
            return null;
        });

        log.OnSystemMessage("a zork b", 0u);

        Assert.Equal("a zork b", seenText);
    }

    [Fact]
    public void ACensoredWordInTheRewriteIsStarredWhenTheLanguageFilterIsOn()
    {
        var log = new ChatLog
        {
            FilterLanguageSource = () => true,
            FilterLanguagePatterns = new[] { "zork" },
        };
        using IDisposable rewrite = log.DisplayRewrites.Register(static _ => "a zork b");

        log.OnSystemMessage("hello", 0u);

        Assert.Equal("a **** b", log.Snapshot()[0].DisplayRewrite);
    }

    [Fact]
    public void APluginsOwnRepostIsOfferedToRewrites()
    {
        var log = new ChatLog();
        var offered = new List<string>();
        using IDisposable rewrite = log.DisplayRewrites.Register(candidate =>
        {
            offered.Add(candidate.Text);
            return null;
        });

        log.OnSelfSent(ChatKind.Tell, "hey", logTextType: 0x04u, targetOrChannel: "Alice");

        Assert.Equal(["hey"], offered);
    }
}
