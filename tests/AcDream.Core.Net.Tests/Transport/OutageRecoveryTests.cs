using System.Net;
using System.Net.Sockets;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Transport;

/// <summary>
/// A few seconds with no route to the server must heal like packet loss once
/// the route returns. ACE parks an ack-only packet in its out-of-order table
/// under the packet sequence it names; if that sequence is a packet ACE does
/// not have yet, the real packet arriving while the gap below it is still
/// open is discarded after its ISAAC key is spent, and every retransmission
/// of it fails the checksum from then on. The session stays up on cleartext
/// acks while nothing the client says is acted on again.
/// </summary>
[Collection(AcDream.Core.Net.Tests.NetProcessStaticsCollection.Name)]
public sealed class OutageRecoveryTests
{
    private static readonly TimeSpan HarnessPatience = TimeSpan.FromSeconds(10);

    [Fact]
    public void AfterAnOutage_TheAckNeverStrandsAnUnsentPacketAtAce()
    {
        var fake = new FakeAceTransport();
        var outage = new OutageTransport(fake);
        bool holdNextAck = false;
        uint swapResendOf = 0;
        fake.Link.Drop(LinkDirection.ClientToServer, (_, bytes) =>
        {
            // Two ordinary adjacent swaps on the wire, nothing lost: the
            // first ack after the outage overtaken by the next message, and
            // one retransmission overtaken by the one after it.
            PacketHeader header = PacketHeader.Unpack(bytes);
            if (holdNextAck && header.Flags == PacketHeaderFlags.AckSequence)
            {
                holdNextAck = false;
                fake.Link.Reorder(LinkDirection.ClientToServer);
            }
            else if (swapResendOf != 0
                     && (header.Flags & PacketHeaderFlags.Retransmission) != 0
                     && header.Sequence == swapResendOf)
            {
                swapResendOf = 0;
                fake.Link.Reorder(LinkDirection.ClientToServer);
            }
            return false;
        });
        var session = new WorldSession(new IPEndPoint(IPAddress.Loopback, 9000), outage)
        {
            TransportClockSource = (fake.Clock.GetTimestamp, fake.Clock.Frequency),
        };
        TextWriter savedError = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null);
            session.Connect("testaccount", "testpassword", TimeSpan.FromSeconds(10));
            session.EnterWorld(0, TimeSpan.FromSeconds(10));
            Assert.Equal(WorldSession.State.InWorld, session.CurrentState);
            int baseline = fake.Model.DispatchedMessages.Count;
            var expected = new List<byte[]>();
            uint talk = 0;
            void Say(string text)
            {
                expected.Add(ChatRequests.BuildTalk(++talk, text));
                session.SendTalk(text);
            }

            // Talking normally; the server acknowledges it.
            Say("before");
            Advance(fake, session, 2.1);
            Assert.Equal(baseline + 1, fake.Model.DispatchedMessages.Count);

            // The route vanishes for a moment: three messages never leave.
            outage.Down = true;
            Say("lost 1");
            Say("lost 2");
            Say("lost 3");
            uint lastLost = session.Transport!.HighestIdSent;
            outage.Down = false;

            // The first ack after the outage is overtaken by the next message.
            holdNextAck = true;
            Advance(fake, session, 2.1);
            long naksBefore = session.Transport.Stats.NakRequestsReceived;
            Say("after");
            Assert.False(holdNextAck, "no ack went out after the outage");

            // ACE asks for the gap; one retransmission is overtaken by the next.
            swapResendOf = lastLost - 1;
            Assert.True(SpinWait.SpinUntil(
                () =>
                {
                    session.Tick();
                    return session.Transport.Stats.NakRequestsReceived > naksBefore;
                },
                HarnessPatience));
            session.Tick();
            Assert.True(swapResendOf == 0, "the retransmission burst never went out");

            // Carry on talking; everything must reach the server in order.
            for (int i = 0; i < 6; i++)
            {
                Say($"later {i}");
                Advance(fake, session, 1.1);
            }
            SpinWait.SpinUntil(
                () =>
                {
                    Advance(fake, session, 0.2);
                    return fake.Model.DispatchedMessages.Count >= baseline + expected.Count;
                },
                HarnessPatience);

            string state =
                $"server lastSeq={fake.Model.LastReceivedPacketSequence} lastLost={lastLost} "
                + $"crcDrops={fake.Model.CrcDropCount} parked={fake.Model.OutOfOrderPacketCount} "
                + $"dispatched={fake.Model.DispatchedMessages.Count - baseline}/{expected.Count}";
            Assert.False(fake.Model.IsTerminated, state);
            Assert.True(0 == fake.Model.CrcDropCount, state);
            Assert.Equal(expected, fake.Model.DispatchedMessages.Skip(baseline).ToList());
        }
        finally
        {
            Console.SetError(savedError);
            session.Dispose();
        }
    }

    private static void Advance(FakeAceTransport fake, WorldSession session, double seconds)
    {
        for (double elapsed = 0; elapsed < seconds - 1e-9; elapsed += 0.1)
        {
            fake.Clock.Advance(TimeSpan.FromSeconds(0.1));
            session.Tick();
            Thread.Sleep(1);
        }
    }

    /// <summary>The route to the server vanishes: sends fail the way the
    /// socket fails them on macOS.</summary>
    private sealed class OutageTransport(FakeAceTransport inner) : IWorldSessionTransport
    {
        public volatile bool Down;

        public void Send(ReadOnlySpan<byte> datagram)
        {
            if (Down)
                throw new SocketException((int)SocketError.NetworkUnreachable);
            inner.Send(datagram);
        }

        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram)
        {
            if (Down)
                throw new SocketException((int)SocketError.NetworkUnreachable);
            inner.Send(remote, datagram);
        }

        public int Receive(Span<byte> destination, TimeSpan timeout, out IPEndPoint? from) =>
            inner.Receive(destination, timeout, out from);

        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            inner.ReceiveAsync(destination, cancellationToken);

        public void Dispose() => inner.Dispose();
    }
}
