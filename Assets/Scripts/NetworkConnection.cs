using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Mirror;
using UnityEngine;

namespace Network
{
    public abstract class NetworkConnection
    {
        /// <summary>Unique identifier for this connection that is assigned by the transport layer.</summary>
        // assigned by transport, this id is unique for every connection on server.
        // clients don't know their own id and they don't know other client's ids.
        public readonly int connectionId;

        /// <summary>Flag that indicates the client has been authenticated.</summary>
        public bool isAuthenticated;

        /// <summary>General purpose object to hold authentication data, character selection, tokens, etc.</summary>
        public object authenticationData;

        /// <summary>A server connection is ready after joining the game world.</summary>
        // TODO move this to ConnectionToClient so the flag only lives on server
        // connections? clients could use NetworkClient.ready to avoid redundant
        // state.
        public bool isReady;

        /// <summary>Last time a message was received for this connection. Includes system and user messages.</summary>
        public float lastMessageTime;

        // batching from server to client & client to server.
        // fewer transport calls give us significantly better performance/scale.
        //
        // for a 64KB max message transport and 64 bytes/message on average, we
        // reduce transport calls by a factor of 1000.
        //
        // depending on the transport, this can give 10x performance.
        //
        // Dictionary<channelId, batch> because we have multiple channels.
        protected Dictionary<int, Batcher> batches = new Dictionary<int, Batcher>();

        /// <summary>last batch's remote timestamp. not interpolated. useful for NetworkTransform etc.</summary>
        // for any given NetworkMessage/Rpc/Cmd/OnSerialize, this was the time
        // on the REMOTE END when it was sent.
        //
        // NOTE: this is NOT in NetworkTime, it needs to be per-connection
        //       because the server receives different batch timestamps from
        //       different connections.
        public double remoteTimeStamp { get; internal set; }

        internal NetworkConnection()
        {
            // set lastTime to current time when creating connection to make
            // sure it isn't instantly kicked for inactivity
            lastMessageTime = Time.time;
        }

        internal NetworkConnection(int networkConnectionId) : this()
        {
            connectionId = networkConnectionId;
        }

        // TODO if we only have Reliable/Unreliable, then we could initialize
        // two batches and avoid this code
        protected Batcher GetBatchForChannelId(int channelId)
        {
            // get existing or create new writer for the channelId
            Batcher batch;
            if (!batches.TryGetValue(channelId, out batch))
            {
                // get max batch size for this channel
                int threshold = Transport.active.GetBatchThreshold(channelId);

                // create batcher
                batch = new Batcher(threshold);
                batches[channelId] = batch;
            }
            return batch;
        }

        // Send stage one: NetworkMessage<T>
        /// <summary>Send a NetworkMessage to this connection over the given channel.</summary>
        public void Send<T>(ushort protoId, T message, int channelId = Channels.Reliable)
            //where T : struct, NetworkMessage
        {
            using (NetworkWriterPooled writer = NetworkWriterPool.Get())
            {
                // pack message
                
                Network.NetworkMessages.Pack(protoId, message, writer);

                // validate packet size immediately.
                // we know how much can fit into one batch at max.
                // if it's larger, log an error immediately with the type <T>.
                // previously we only logged in Update() when processing batches,
                // but there we don't have type information anymore.
                int max = Mirror.NetworkMessages.MaxMessageSize(channelId);
                if (writer.Position > max)
                {
                    Debug.LogError($"NetworkConnection.Send: message of type {typeof(T)} with a size of {writer.Position} bytes is larger than the max allowed message size in one batch: {max}.\nThe message was dropped, please make it smaller.");
                    return;
                }

                // send allocation free
                //NetworkDiagnostics.OnSend(message, channelId, writer.Position, 1);
                Send(writer.ToArraySegment(), channelId);
            }
        }

        // Send stage two: serialized NetworkMessage as ArraySegment<byte>
        // internal because no one except Mirror should send bytes directly to
        // the client. they would be detected as a message. send messages instead.
        // => make sure to validate message<T> size before calling Send<byte>!
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal virtual void Send(ArraySegment<byte> segment, int channelId = Channels.Reliable)
        {
            //Debug.Log($"ConnectionSend {this} bytes:{BitConverter.ToString(segment.Array, segment.Offset, segment.Count)}");

            // add to batch no matter what.
            // batching will try to fit as many as possible into MTU.
            // but we still allow > MTU, e.g. kcp max packet size 144kb.
            // those are simply sent as single batches.
            //
            // IMPORTANT: do NOT send > batch sized messages directly:
            // - data race: large messages would be sent directly. small
            //   messages would be sent in the batch at the end of frame
            // - timestamps: if batching assumes a timestamp, then large
            //   messages need that too.
            //
            // NOTE: we ALWAYS batch. it's not optional, because the
            //       receiver needs timestamps for NT etc.
            //
            // NOTE: we do NOT ValidatePacketSize here yet. the final packet
            //       will be the full batch, including timestamp.
            GetBatchForChannelId(channelId).AddMessage(segment, NetworkTime.localTime);
        }
    }
}
