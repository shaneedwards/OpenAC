using AcDream.Core.Chat;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>The shared plugin surface's side of a display-only hide.</summary>
public sealed class RuntimeAutomationSurfaceChatDisplayFilterTests
{
    [Fact]
    public void AHiddenLineStillReachesReceivedAndCaptureMessagesButNotTheTranscript()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var seen = new List<string>();
        surface.Chat.Received += message => seen.Add(message.Text);
        using IDisposable hide = surface.Chat.RegisterDisplayFilter(
            static candidate => candidate.Text.Contains(
                "hide me",
                StringComparison.Ordinal));

        runtime.CommunicationOwner.Chat.OnSystemMessage("please hide me", 0u);
        runtime.CommunicationOwner.Chat.OnSystemMessage("keep me", 0u);

        Assert.Equal(["please hide me", "keep me"], seen);
        ChatEntry shown = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("keep me", shown.Text);
        PluginChatMessage[] captured = [.. surface.CaptureMessages(0)];
        Assert.Equal(2, captured.Length);
    }

    [Fact]
    public void APluginsOwnPostedLineIsOfferedToItsOwnDisplayFilter()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var offered = new List<string>();
        using IDisposable hide = surface.Chat.RegisterDisplayFilter(candidate =>
        {
            offered.Add(candidate.Text);
            return false;
        });

        surface.Chat.PostMessage("my own line", 0);

        Assert.Equal(["my own line"], offered);
    }

    [Fact]
    public void AFilterInstalledBeforeLoginStillAppliesToTheNextSession()
    {
        using var surface = new RuntimeAutomationSurface();
        using IDisposable hide = surface.Chat.RegisterDisplayFilter(static _ => true);

        using var runtime = GameRuntimeTestFactory.Create();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        runtime.CommunicationOwner.Chat.OnSystemMessage("hidden", 0u);

        Assert.Equal(0, runtime.CommunicationOwner.Chat.Count);
    }

    [Fact]
    public void UnbindingRemovesTheSurfaceDisplayFiltersFromTheSessionsLog()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        using IDisposable hide = surface.Chat.RegisterDisplayFilter(static _ => true);

        surface.Unbind();
        runtime.CommunicationOwner.Chat.OnSystemMessage("shown", 0u);

        Assert.Equal(1, runtime.CommunicationOwner.Chat.Count);
    }
}
