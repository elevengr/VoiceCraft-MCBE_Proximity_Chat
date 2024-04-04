﻿using System.Collections.Concurrent;
using System.Net;
using VoiceCraft.Core;
using VoiceCraft.Core.Packets;
using VoiceCraft.Core.Packets.VoiceCraft;

namespace VoiceCraft.Network
{
    public class NetPeer : Disposable
    {
        public const int ResendTime = 200;
        public const int RetryResendTime = 500;
        public const int MaxSendRetries = 20;
        public const int MaxRecvBufferSize = 30; //30 packets.

        public delegate void PacketReceived(NetPeer peer, VoiceCraftPacket packet);
        public event PacketReceived? OnPacketReceived;
        private uint Sequence;
        private uint NextSequence;
        private CancellationTokenSource CTS { get; } = new CancellationTokenSource();
        private ConcurrentDictionary<uint, VoiceCraftPacket> ReliabilityQueue { get; set; }
        private ConcurrentDictionary<uint, VoiceCraftPacket> ReceiveBuffer { get; set; }

        /// <summary>
        /// Defines wether the client is sucessfully connected and accepted.
        /// </summary>
        public bool Connected { get; private set; }

        /// <summary>
        /// Endpoint of the NetPeer.
        /// </summary>
        public EndPoint EP { get; set; }

        /// <summary>
        /// The cancellation token used to stop listening on the socket for this peer.
        /// </summary>
        public CancellationToken Token { get => CTS.Token; }

        /// <summary>
        /// When the client was last active.
        /// </summary>
        public long LastActive { get; set; } = Environment.TickCount64;

        /// <summary>
        /// The ID of the NetPeer, Used to update the endpoint if invalid.
        /// </summary>
        public long ID { get; set; } //Not secure enough but it'll do.

        /// <summary>
        /// The key for the NetPeer, Used as a public shareable Id.
        /// </summary>
        public short Key { get; set; }

        /// <summary>
        /// Send Queue.
        /// </summary>
        public ConcurrentQueue<VoiceCraftPacket> SendQueue { get; set; }

        public NetPeer(EndPoint ep, long Id, short key)
        {
            EP = ep;
            ID = Id;
            Key = key;
            SendQueue = new ConcurrentQueue<VoiceCraftPacket>();
            ReliabilityQueue = new ConcurrentDictionary<uint, VoiceCraftPacket>();
            ReceiveBuffer = new ConcurrentDictionary<uint, VoiceCraftPacket>();
        }

        public void AddToSendBuffer(VoiceCraftPacket packet)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(NetPeer));

            if (packet.IsReliable)
            {
                packet.Sequence = Sequence;
                packet.ResendTime = Environment.TickCount64 + ResendTime;
                ReliabilityQueue.TryAdd(packet.Sequence, packet); //If reliable, Add to reliability queue. ResendTime is determined by the application.
                Sequence++;
            }

            SendQueue.Enqueue(packet);
        }

        public bool AddToReceiveBuffer(VoiceCraftPacket packet)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(NetPeer));

            LastActive = Environment.TickCount64;

            if(ReceiveBuffer.Count >= MaxRecvBufferSize && packet.Sequence != NextSequence)
                return false; //We can reset the connection because of too many incorrect packets, however that is up to the application.

            if(!packet.IsReliable)
            {
                OnPacketReceived?.Invoke(this, packet);
                return true; //Not reliable, We can just say it's received.
            }

            ReceiveBuffer.TryAdd(packet.Sequence, packet); //Add it in, TryAdd does not replace an old packet.
            AddToSendBuffer(new Ack() { Id = ID, PacketSequence = packet.Sequence }); //Acknowledge packet by sending the Ack packet.

            foreach(var p in ReceiveBuffer)
            {
                if (p.Key == NextSequence && ReceiveBuffer.TryRemove(p)) //Remove packet and notify listeners.
                {
                    NextSequence++; //Update next expected packet.
                    OnPacketReceived?.Invoke(this, p.Value);
                }
                else if (p.Key < NextSequence)
                    ReceiveBuffer.TryRemove(p); //Remove old packet.
            }
            return true;
        }

        public void ResendPackets()
        {
            foreach (var packet in ReliabilityQueue)
            {
                if (packet.Value.ResendTime <= Environment.TickCount64)
                {
                    packet.Value.ResendTime = Environment.TickCount64 + RetryResendTime; //More delay.
                    packet.Value.Retries++;
                    SendQueue.Enqueue(packet.Value);
                }
            }
        }

        public void AcceptLogin()
        {
            if (!Connected)
            {
                Connected = true;
                AddToSendBuffer(new Accept() { Id = ID, Key = Key });
            }
        }

        public void AcknowledgePacket(uint packetId)
        {
            ReliabilityQueue.TryRemove(packetId, out var _);
        }

        public static long GenerateId()
        {
            return Random.Shared.NextInt64(long.MinValue + 1, long.MaxValue); //long.MinValue is used to specify no Id.
        }

        public static short GenerateKey()
        {
            return (short)Random.Shared.Next(short.MinValue + 1, short.MaxValue); //short.MinValue is used to specify no Key.
        }

        public void Reset()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(NetPeer));

            SendQueue.Clear();
            ReliabilityQueue.Clear();
            ReceiveBuffer.Clear();
            NextSequence = 0;
            Sequence = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!CTS.IsCancellationRequested)
                    CTS.Cancel();

                CTS.Dispose();
                SendQueue.Clear();
                ReliabilityQueue.Clear();
                ReceiveBuffer.Clear();
                Connected = false;
                OnPacketReceived = null;
            }
        }
    }
}
