using AcDream.Core.Chat;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>The shared plugin surface's side of a display-only rewrite.</summary>
public sealed class RuntimeAutomationSurfaceChatDisplayRewriteTests
{
    [Fact]
    public void ARewriteChangesTheTranscriptButNotReceivedOrCaptureMessages()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var seen = new List<string>();
        surface.Chat.Received += message => seen.Add(message.Text);
        using IDisposable rewrite = surface.Chat.RegisterDisplayRewrite(
            static candidate => candidate.Text == "rewrite me" ? "rewritten" : null);

        runtime.CommunicationOwner.Chat.OnSystemMessage("rewrite me", 0u);

        Assert.Equal(["rewrite me"], seen);
        ChatEntry shown = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("rewrite me", shown.Text);
        Assert.Equal("rewritten", shown.DisplayRewrite);
        PluginChatMessage[] captured = [.. surface.CaptureMessages(0)];
        Assert.Equal("rewrite me", Assert.Single(captured).Text);
    }

    [Fact]
    public void APluginsOwnPostedLineIsOfferedToItsOwnDisplayRewrite()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var offered = new List<string>();
        using IDisposable rewrite = surface.Chat.RegisterDisplayRewrite(candidate =>
        {
            offered.Add(candidate.Text);
            return null;
        });

        surface.Chat.PostMessage("my own line", 0);

        Assert.Equal(["my own line"], offered);
    }

    [Fact]
    public void ARewriteInstalledBeforeLoginStillAppliesToTheNextSession()
    {
        using var surface = new RuntimeAutomationSurface();
        using IDisposable rewrite = surface.Chat.RegisterDisplayRewrite(static _ => "rewritten");

        using var runtime = GameRuntimeTestFactory.Create();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        runtime.CommunicationOwner.Chat.OnSystemMessage("hello", 0u);

        Assert.Equal("rewritten", runtime.CommunicationOwner.Chat.Snapshot()[0].DisplayRewrite);
    }

    [Fact]
    public void UnbindingRemovesTheSurfaceDisplayRewritesFromTheSessionsLog()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        using IDisposable rewrite = surface.Chat.RegisterDisplayRewrite(static _ => "rewritten");

        surface.Unbind();
        runtime.CommunicationOwner.Chat.OnSystemMessage("hello", 0u);

        Assert.Null(runtime.CommunicationOwner.Chat.Snapshot()[0].DisplayRewrite);
    }
}
