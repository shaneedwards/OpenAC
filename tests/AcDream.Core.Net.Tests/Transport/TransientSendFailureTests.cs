using System.Buffers.Binary;
using System.Net.Sockets;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Transport;

namespace AcDream.Core.Net.Tests.Transport;

[Collection(AcDream.Core.Net.Tests.NetProcessStaticsCollection.Name)]
public sealed class TransientSendFailureTests
{
    private const uint ClientSeed = 0x11AA22BBu;
    private const uint ServerSeed = 0x33CC44DDu;
    private const ushort SessionClientId = 0x1234;
    private const ushort SessionIteration = 0x0007;

    [Fact]
    public void TransientSendError_IsSwallowed_DatagramStaysCachedForTheNak()
    {
        var sent = new List<byte[]>();
        bool throwOnce = true;
        using var transport = new ReliableTransport(
            MakeIsaac(ClientSeed),
            MakeIsaac(ServerSeed),
            SessionClientId,
            SessionIteration,
            datagram =>
            {
                if (throwOnce)
                {
                    throwOnce = false;
                    throw new SocketException((int)SocketError.NetworkUnreachable);
                }
                sent.Add(datagram.ToArray());
            });

        // The throwing send must not escape SendGameMessage.
        transport.Outbound.SendGameMessage(MakeMessage(0xA1), GameMessageGroup.UIQueue);

        Assert.Empty(sent);
        Assert.Equal(0, transport.Stats.PacketsSent);

        // The server asks for it again: it was cached despite the failed send.
        Nak(transport.Outbound, 2u);
        transport.Outbound.TransmitPendingResends();

        Assert.Single(sent);
        Assert.Equal(0, transport.Stats.UncachedNakIds);
    }

    [Fact]
    public void NonTransientSendError_StillThrows()
    {
        using var transport = new ReliableTransport(
            MakeIsaac(ClientSeed),
            MakeIsaac(ServerSeed),
            SessionClientId,
            SessionIteration,
            _ => throw new SocketException((int)SocketError.ConnectionReset));

        Assert.Throws<SocketException>(() =>
            transport.Outbound.SendGameMessage(MakeMessage(0xA1), GameMessageGroup.UIQueue));
    }

    [Fact]
    public void FailedSend_LogsOnceAtTheStartOfAnOutage_NotPerPacket()
    {
        TextWriter saved = Console.Error;
        var captured = new StringWriter();
        bool fail = true;
        using var transport = new ReliableTransport(
            MakeIsaac(ClientSeed),
            MakeIsaac(ServerSeed),
            SessionClientId,
            SessionIteration,
            _ =>
            {
                if (fail)
                    throw new SocketException((int)SocketError.NetworkUnreachable);
            });
        try
        {
            Console.SetError(captured);

            // A run of failures: only the first should log.
            transport.Outbound.SendGameMessage(MakeMessage(1), GameMessageGroup.UIQueue);
            transport.Outbound.SendGameMessage(MakeMessage(2), GameMessageGroup.UIQueue);
            transport.Outbound.SendGameMessage(MakeMessage(3), GameMessageGroup.UIQueue);

            // A success resets the outage, so the next failure logs again.
            fail = false;
            transport.Outbound.SendGameMessage(MakeMessage(4), GameMessageGroup.UIQueue);
            fail = true;
            transport.Outbound.SendGameMessage(MakeMessage(5), GameMessageGroup.UIQueue);
        }
        finally
        {
            Console.SetError(saved);
        }

        int lines = captured.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains("[session]", StringComparison.Ordinal));
        Assert.Equal(2, lines);
    }

    [Fact]
    public void AckPacket_NamesTheWatermark_WhileAnUnsentPacketIsUnacknowledged()
    {
        bool fail = false;
        using var transport = new ReliableTransport(
            MakeIsaac(ClientSeed),
            MakeIsaac(ServerSeed),
            SessionClientId,
            SessionIteration,
            _ =>
            {
                if (fail)
                    throw new SocketException((int)SocketError.NetworkUnreachable);
            });
        TextWriter saved = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null);
            OutboundFlowQueue outbound = transport.Outbound;

            outbound.SendGameMessage(MakeMessage(1), GameMessageGroup.UIQueue); // 2
            outbound.OnAckSequence(2u);
            Assert.Equal(outbound.HighestIdSent, outbound.AckPacketSequence);

            fail = true;
            outbound.SendGameMessage(MakeMessage(2), GameMessageGroup.UIQueue); // 3, never sent
            outbound.SendGameMessage(MakeMessage(3), GameMessageGroup.UIQueue); // 4, never sent
            fail = false;
            outbound.SendGameMessage(MakeMessage(4), GameMessageGroup.UIQueue); // 5
            Assert.Equal(5u, outbound.HighestIdSent);
            Assert.Equal(2u, outbound.AckPacketSequence);

            // Acknowledged up to the last unsent packet, not yet past it.
            outbound.OnAckSequence(4u);
            Assert.Equal(4u, outbound.AckPacketSequence);

            // Past it: back to the retail convention.
            outbound.OnAckSequence(5u);
            Assert.Equal(5u, outbound.AckPacketSequence);
        }
        finally
        {
            Console.SetError(saved);
        }
    }

    private static void Nak(OutboundFlowQueue queue, params uint[] ids)
    {
        byte[] bytes = new byte[ids.Length * 4];
        for (int i = 0; i < ids.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), ids[i]);
        queue.OnRetransmitRequest(bytes, ids.Length);
    }

    private static byte[] MakeMessage(byte marker) =>
        new byte[] { marker, 0x11, 0x22, 0x33, 0x00, 0x00, 0x00, 0x00 };

    private static IsaacRandom MakeIsaac(uint seed)
    {
        Span<byte> seedBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seedBytes, seed);
        return new IsaacRandom(seedBytes);
    }
}
