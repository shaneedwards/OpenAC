using System.Collections.Concurrent;
using System.Net.Sockets;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.HostParity.Tests;

// Both host arms talk to the real IPC hub; no peer announcement files are involved.
internal sealed class ParityPeerHub : IDisposable
{
    private static readonly ConcurrentDictionary<string, ParityPeerHub> Active = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _server;
    private readonly IDisposable _subscription;
    internal ParityArm Arm { get; }
    internal PeerHubEndpoint Endpoint { get; }
    internal ParityPeerHub(ParityArm arm)
    {
        Arm = arm;
        Endpoint = PeerHubEndpoint.ForDirectory(arm.PeerDirectory);
        _server = Task.Run(() => new PeerHubServer(Endpoint).RunAsync(_lifetime.Token));
        try { WaitUntilListening(); }
        catch { _lifetime.Cancel(); throw; }
        Active[arm.PeerDirectory] = this;
        _subscription = arm.Host.Automation.Network.Subscribe(
            PluginPeerCapabilities.ClientState | PluginPeerCapabilities.Casts | PluginPeerCapabilities.Commands)!;
        Assert.NotNull(_subscription);
        arm.Advance();
    }
    internal static ParityPeerHub Find(string directory) => Active[directory];

    // A Unix socket connect fails at once until the hub listens, where a named pipe connect waits.
    private void WaitUntilListening()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            if (_server.IsCompleted)
            {
                _server.GetAwaiter().GetResult();
                throw new InvalidOperationException("The parity peer hub stopped before it listened.");
            }
            try
            {
                Endpoint.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult().Dispose();
                return;
            }
            catch (Exception e) when (e is IOException or SocketException or TimeoutException)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("The parity peer hub never started listening.", e);
                Thread.Sleep(2);
            }
        }
    }
    internal void Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("IPC parity condition was not observed.");
            Arm.Advance();
            Thread.Sleep(1);
        }
    }
    public void Dispose()
    {
        _subscription.Dispose();
        Active.TryRemove(Arm.PeerDirectory, out _);
        _lifetime.Cancel();
        _server.GetAwaiter().GetResult();
        _lifetime.Dispose();
    }
}

internal sealed class ParityPeerClient : IDisposable
{
    private readonly ParityPeerHub _hub;
    private readonly LocalPluginPeerRegistry _registry;
    private readonly LocalPeerTransport _transport;
    private readonly IDisposable _subscription;
    private readonly TimeProvider _time;
    private readonly List<(LocalPluginCast Cast, DateTimeOffset At)> _casts = [];
    private readonly List<LocalPluginCommand> _commands = [];
    internal uint ClientId => _registry.ClientId;
    internal ParityPeerClient(string directory, TimeProvider? timeProvider = null, Guid? instanceId = null)
    {
        _hub = ParityPeerHub.Find(directory);
        _time = timeProvider ?? TimeProvider.System;
        _registry = new LocalPluginPeerRegistry(directory, instanceId: instanceId, memoryOnly: true);
        _transport = new LocalPeerTransport(_hub.Endpoint, _registry, message => _registry.PushCommand(message),
            startHub: () => throw new InvalidOperationException("Parity hub should already be listening."));
        _subscription = _transport.Subscribe(PluginPeerCapabilities.ClientState | PluginPeerCapabilities.Casts | PluginPeerCapabilities.Commands)!;
        Publish(new PluginNetworkClient(ClientId, 0x50000003u, "Onlooker",
            _hub.Arm.Host.Automation.Character.WorldName, default, ["squad"], 100, 100, 100, 100, 100, 100, 0));
    }
    internal bool RecordCast(LocalPluginCast cast) { _casts.Add((cast, _time.GetUtcNow())); return true; }
    internal bool RecordCommand(LocalPluginCommand command) { _commands.Add(command); return true; }
    internal void Publish(PluginNetworkClient client)
    {
        _transport.UpdateSelf(client with { ClientId = ClientId });
        _hub.Until(() => _registry.CaptureRemoteClients().Count > 0 && _transport.HasCastDemand
            && _hub.Arm.Host.Automation.Network.CaptureClients().Any(c => c.ClientId == ClientId));
        long sequence = 0;
        foreach (var (cast, at) in _casts)
        {
            double remaining = Math.Max(0, cast.DurationSeconds - (DateTimeOffset.UtcNow - at).TotalSeconds);
            Assert.True(_transport.SendCast(new PluginPeerCast(++sequence, ClientId, cast.CasterObjectId,
                cast.TargetObjectId, cast.SpellId, cast.EffectiveSkill, remaining, cast.Landed)));
        }
        if (_casts.Count > 0) _hub.Until(() => _hub.Arm.Host.Automation.Network.CaptureCasts(0).Count >= _casts.Count);
        foreach (var command in _commands)
            Assert.True(_transport.SendCommand(command.Line, command.Tags.ToArray(), command.DelayMilliseconds));
        if (_commands.Count > 0)
        {
            // A subsequent state update is ordered after all command frames on this connection.
            _transport.UpdateSelf(client with { ClientId = ClientId, Name = "Delivered" });
            _hub.Until(() => _hub.Arm.Host.Automation.Network.CaptureClients().Any(c => c.ClientId == ClientId && c.Name == "Delivered"));
            _hub.Arm.Advance();
        }
        _casts.Clear(); _commands.Clear();
    }
    internal IReadOnlyList<PluginNetworkClient> CaptureRemoteClients()
    {
        _hub.Arm.Host.Automation.Network.TryCaptureSelf(out var self);
        _hub.Until(() => _registry.CaptureRemoteClients().Any(c => c.PlayerId == self.PlayerId
            && c.CurrentHealth == self.CurrentHealth && c.CurrentMana == self.CurrentMana
            && c.CurrentStamina == self.CurrentStamina && c.Tags.SequenceEqual(self.Tags)));
        return _registry.CaptureRemoteClients();
    }
    internal IReadOnlyList<PluginPeerCast> CaptureRemoteCasts(long after, string world, uint player)
    {
        _hub.Until(() => _registry.CaptureRemoteCasts(after, world, player).Count > 0);
        return _registry.CaptureRemoteCasts(after, world, player);
    }
    internal IReadOnlyList<LocalPluginPeerCommand> CaptureRemoteCommands(long after, string world, uint player, IReadOnlyList<string> tags)
    {
        _hub.Until(() => _registry.CaptureRemoteCommands(after, world, player, tags).Count > 0);
        return _registry.CaptureRemoteCommands(after, world, player, tags);
    }
    public void Dispose() { _subscription.Dispose(); _transport.Dispose(); _registry.Dispose(); }
}
