﻿using NUnit.Framework;
using Pixockets;
using System;
using System.IO;
using System.Net;
using System.Threading;
using Pixockets.Pools;
using UnitTests.Mock;

namespace UnitTests
{
    [TestFixture]
    public class ReliableSendTests
    {
        private MockSmartCallbacks _cbs;
        private BufferPoolBase _bufferPool;
        private MockSock _bareSock;
        private SmartSock _sock;

        [SetUp]
        public void Setup()
        {
            _cbs = new MockSmartCallbacks();
            _bareSock = new MockSock();
            _bufferPool = new ByteBufferPool();
            _sock = new SmartSock(_bufferPool, _bareSock, _cbs);
        }

        [TearDown]
        public void TearDown()
        {
            _sock.Close();
        }

        [Test]
        public void SendReliable()
        {
            var endPoint = new IPEndPoint(IPAddress.Loopback, 23452);
            Utils.SendConnectRequest(_bareSock, endPoint, _bufferPool);
            var receivedPacket = new ReceivedSmartPacket();
            Assert.IsFalse(_sock.Receive(ref receivedPacket));

            var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(123456789), 0, 4);
            var buffer = ms.ToArray();
            _sock.Send(endPoint, buffer, 0, buffer.Length, true);

            Assert.AreEqual(2, _bareSock.Sends.Count);

            var packetToSend = _bareSock.LastSend;
            var header = new PacketHeader();
            header.Init(packetToSend.Buffer, packetToSend.Offset);
            Assert.AreEqual(buffer.Length + header.HeaderLength, header.Length);
            Assert.AreEqual(123456789, BitConverter.ToInt32(packetToSend.Buffer, header.HeaderLength));
            Assert.AreEqual(0, header.SeqNum);
            Assert.IsTrue(header.GetNeedAck());
            Assert.IsFalse(packetToSend.PutBufferToPool, "Reliable packets should wait for Ack before going to pool");
        }

        [Test]
        public void ReceiveReliable()
        {
            var endPoint = new IPEndPoint(IPAddress.Loopback, 23452);
            Utils.SendConnectRequest(_bareSock, endPoint, _bufferPool);

            var buffer1 = FakeSentPacket(123);
            _bareSock.FakeReceive(buffer1, 0, buffer1.Length, endPoint);

            var buffer2 = FakeSentPacket(124);
            _bareSock.FakeReceive(buffer2, 0, buffer2.Length, endPoint);

            var receivedPacket = new ReceivedSmartPacket();
            Assert.IsTrue(_sock.Receive(ref receivedPacket));
            Assert.IsTrue(_sock.Receive(ref receivedPacket));

            // Ack not sent yet, just connect response
            Assert.AreEqual(1, _bareSock.Sends.Count);
            _sock.Tick();
            // Ack sent
            Assert.AreEqual(2, _bareSock.Sends.Count);

            // Make sure ack sent
            var ackHeader = new PacketHeader();
            var packet = _bareSock.LastSend;
            ackHeader.Init(packet.Buffer, packet.Offset);
            Assert.GreaterOrEqual(packet.Buffer.Length, ackHeader.Length);
            Assert.GreaterOrEqual(packet.Buffer.Length, ackHeader.HeaderLength);
            Assert.Contains(123, ackHeader.Acks);
            Assert.Contains(124, ackHeader.Acks);
            Assert.IsFalse(ackHeader.GetNeedAck());
            Assert.IsTrue((ackHeader.Flags & PacketHeader.ContainsAck) != 0);
            Assert.IsFalse((ackHeader.Flags & PacketHeader.ContainsSeq) != 0, "No need to write SeqNum in pure Acknowledgement packet");
            Assert.IsTrue(ackHeader.SessionId != PacketHeader.EmptySessionId, "Acks should already have Non-Empty SessionId");
        }

        [Test]
        public void AcksSentWithPayload()
        {
            var endPoint = new IPEndPoint(IPAddress.Loopback, 23452);
            Utils.SendConnectRequest(_bareSock, endPoint, _bufferPool);

            var buffer1 = FakeSentPacket(234);
            _bareSock.FakeReceive(buffer1, 0, buffer1.Length, endPoint);

            var buffer2 = FakeSentPacket(235);
            _bareSock.FakeReceive(buffer2, 0, buffer2.Length, endPoint);

            var receivedPacket = new ReceivedSmartPacket();
            Assert.IsTrue(_sock.Receive(ref receivedPacket));
            Assert.IsTrue(_sock.Receive(ref receivedPacket));

            // Ack not sent yet, just connect response
            Assert.AreEqual(1, _bareSock.Sends.Count);

            var payload = BitConverter.GetBytes(987654321);
            _sock.Send(endPoint, payload, 0, payload.Length, true);
            // Msg with acks sent
            Assert.AreEqual(2, _bareSock.Sends.Count);

            // Make sure ack sent
            var ackHeader = new PacketHeader();
            var packet = _bareSock.LastSend;
            ackHeader.Init(packet.Buffer, packet.Offset);
            Assert.GreaterOrEqual(packet.Buffer.Length, ackHeader.Length);
            Assert.GreaterOrEqual(packet.Buffer.Length, ackHeader.HeaderLength);
            Assert.Contains(234, ackHeader.Acks);
            Assert.Contains(235, ackHeader.Acks);
            Assert.IsTrue(ackHeader.GetNeedAck());
            Assert.IsTrue((ackHeader.Flags & PacketHeader.ContainsAck) != 0);
        }

        [Test]
        public void ReSendAfterTimeout()
        {
            _sock.AckTimeout = 1;

            var endPoint = new IPEndPoint(IPAddress.Loopback, 23452);
            Utils.SendConnectRequest(_bareSock, endPoint, _bufferPool);
            var receivedPacket = new ReceivedSmartPacket();
            Assert.IsFalse(_sock.Receive(ref receivedPacket));

            var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(123456789), 0, 4);
            var buffer = ms.ToArray();
            _sock.Send(endPoint, buffer, 0, buffer.Length, true);

            Thread.Sleep(20);
            _sock.Tick();

            Assert.AreEqual(3, _bareSock.Sends.Count);
            var packetToSend = _bareSock.LastSend;
            var header = new PacketHeader();
            header.Init(packetToSend.Buffer, packetToSend.Offset);
            Assert.AreEqual(buffer.Length + header.HeaderLength, header.Length);
            Assert.AreEqual(123456789, BitConverter.ToInt32(packetToSend.Buffer, header.HeaderLength));
            Assert.AreEqual(0, header.SeqNum);
            Assert.IsTrue(header.GetNeedAck());
        }

        [Test]
        public void NotReSendAfterAckAndTimeout()
        {
            _sock.AckTimeout = 1;

            var remoteEndPoint = new IPEndPoint(IPAddress.Loopback, 23452);
            Utils.SendConnectRequest(_bareSock, remoteEndPoint, _bufferPool);
            var receivedPacket = new ReceivedSmartPacket();
            Assert.IsFalse(_sock.Receive(ref receivedPacket));

            var buffer = BitConverter.GetBytes(123456789);
            _sock.Send(remoteEndPoint, buffer, 0, buffer.Length, true);

            var sent = _bareSock.Sends[1];
            var headerSent = new PacketHeader();
            headerSent.Init(sent.Buffer, sent.Offset);

            var ackHeader = new PacketHeader();
            ackHeader.AddAck(headerSent.SeqNum);
            ackHeader.Length = (ushort)ackHeader.HeaderLength;
            buffer = _bufferPool.Get(ackHeader.Length);
            ackHeader.WriteTo(buffer, 0);
            _bareSock.FakeReceive(buffer, 0, ackHeader.Length, new IPEndPoint(IPAddress.Loopback, 23452));

            receivedPacket = new ReceivedSmartPacket();
            // Just ack in the packet, no payload
            Assert.IsFalse(_sock.Receive(ref receivedPacket));

            Thread.Sleep(20);
            _sock.Tick();

            Assert.AreEqual(2, _bareSock.Sends.Count);
        }

        private static byte[] FakeSentPacket(ushort seqNum)
        {
            var header = new PacketHeader();
            header.SetNeedAck();
            header.SetSeqNum(seqNum);
            header.Length = (ushort)(header.HeaderLength + 4);
            var ms = new MemoryStream();
            header.WriteTo(ms);
            ms.Write(BitConverter.GetBytes(456 * seqNum), 0, 4);
            var buffer = ms.ToArray();
            return buffer;
        }
    }
}
