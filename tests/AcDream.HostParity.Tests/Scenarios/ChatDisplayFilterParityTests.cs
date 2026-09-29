using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>A tell a plugin hides or rewrites on the display, on both clients.</summary>
public sealed class ChatDisplayFilterParityTests
{
    private const uint Speaker = 0x5000_0033u;

    [Fact]
    public void AHiddenTellReachesReceivedButNotTheFeedOrTheReplyTargetOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var seen = new List<PluginChatMessage>();
            arm.Host.Automation.Chat.Received += seen.Add;
            using IDisposable hide = arm.Host.Automation.Chat.RegisterDisplayFilter(
                static candidate => candidate.Kind == 3);

            transcript.Step("a player tells the character something");
            arm.Server.Tell(
                "hello",
                "Bob",
                senderGuid: Speaker,
                targetGuid: ParityWorld.Player,
                chatType: 0x03u);
            arm.Advance();

            PluginChatMessage line = Assert.Single(seen);
            transcript.Record("received", line.Text);
            transcript.Record(
                "feedCount",
                arm.Runtime.CommunicationOwner.ChatFeed.Snapshot().Count);
            transcript.Record(
                "replyTarget",
                arm.Runtime.CommunicationOwner.CommandTargets.LastIncomingTellSender);
            Assert.Equal("hello", line.Text);
            Assert.Empty(arm.Runtime.CommunicationOwner.ChatFeed.Snapshot());
            Assert.Null(arm.Runtime.CommunicationOwner.CommandTargets.LastIncomingTellSender);
        });

    [Fact]
    public void ARewrittenTellShowsTheReplacedBodyButReceivedSeesTheOriginalOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var seen = new List<PluginChatMessage>();
            arm.Host.Automation.Chat.Received += seen.Add;
            using IDisposable rewrite = arm.Host.Automation.Chat.RegisterDisplayRewrite(
                static candidate => candidate.Kind == 3 ? "rewritten" : null);

            transcript.Step("a player tells the character something");
            arm.Server.Tell(
                "hello",
                "Bob",
                senderGuid: Speaker,
                targetGuid: ParityWorld.Player,
                chatType: 0x03u);
            arm.Advance();

            PluginChatMessage line = Assert.Single(seen);
            transcript.Record("received", line.Text);
            transcript.Record(
                "shown", arm.Runtime.CommunicationOwner.ChatFeed.Snapshot()[0].Text);
            transcript.Record(
                "replyTarget",
                arm.Runtime.CommunicationOwner.CommandTargets.LastIncomingTellSender);
            Assert.Equal("hello", line.Text);
            Assert.Contains(
                "rewritten", arm.Runtime.CommunicationOwner.ChatFeed.Snapshot()[0].Text);
            Assert.Equal(
                "Bob", arm.Runtime.CommunicationOwner.CommandTargets.LastIncomingTellSender);
        });
}
