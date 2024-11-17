using Mirror;
using Google.Protobuf;
using System.Collections.Generic;
using System;
using System.Text;
using UnityEngine;

namespace Network
{
    public static class NetworkMessages
    {
        // size of message id header in bytes
        public const int IdSize = sizeof(ushort);

        // Id <> Type lookup for debugging, profiler, etc.
        // important when debugging messageId errors!
        public static readonly Dictionary<ushort, Type> Lookup =
            new Dictionary<ushort, Type>();

        public static void LogTypes()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("NetworkMessageIds:");
            foreach (KeyValuePair<ushort, Type> kvp in Lookup)
            {
                builder.AppendLine($"  Id={kvp.Key} = {kvp.Value}");
            }
            Debug.Log(builder.ToString());
        }

        // max message content size (without header) calculation for convenience
        // -> Transport.GetMaxPacketSize is the raw maximum
        // -> Every message gets serialized into <<id, content>>
        // -> Every serialized message get put into a batch with one timestamp per batch
        // -> Every message in a batch has a varuint size header.
        //    use the worst case VarUInt size for the largest possible
        //    message size = int.max.
        public static int MaxContentSize(int channelId)
        {
            // calculate the max possible size that can fit in a batch
            int transportMax = Transport.active.GetMaxPacketSize(channelId);
            return transportMax - IdSize - Batcher.MaxMessageOverhead(transportMax);
        }

        // max message size which includes header + content.
        public static int MaxMessageSize(int channelId) =>
            MaxContentSize(channelId) + IdSize;

        public static void Pack<T>(ushort protoId, T message, NetworkWriter writer) where T : IMessage
        {
            writer.WriteUShort(protoId);
            writer.Write(message.ToByteArray());
            //Msg.MessageResult.
            //writer.write
        }

        // read only the message id.
        // common function in case we ever change the header size.
        public static bool UnpackId(NetworkReader reader, out ushort messageId)
        {
            // read message type
            try
            {
                messageId = reader.ReadUShort();
                return true;
            }
            catch (System.IO.EndOfStreamException)
            {
                messageId = 0;
                return false;
            }
        }

        // version for handlers with channelId
        // inline! only exists for 20-30 messages and they call it all the time.
        internal static NetworkMessageDelegate WrapHandler<T, C>(Action<C, T, int> handler, int messageId, bool requireAuthentication, bool exceptionsDisconnect)
            where T : IMessage
            where C : NetworkConnection
            => (conn, reader, channeldId) =>
            {
                T message = default;
                int startPos = reader.Position;
                try
                {
                    if (requireAuthentication && !conn.isAuthenticated)
                    {
                        // message requires authentication, but the connection was not authenticated
                        Debug.LogWarning($"Disconnecting connection: {conn}. Received message {typeof(T)} that required authentication, but the user has not authenticated yet");
                        conn.Disconnect();
                        return;
                    }

                    var parser = NetMsgConfig.GetMsgParser(messageId);
                    var bytes = reader.ReadBytes(reader.Remaining);
                    message = (T)parser.ParseFrom(bytes);
                }
                catch (Exception exception)
                {
                    // should we disconnect on exceptions?
                    if (exceptionsDisconnect)
                    {
                        Debug.LogError($"Disconnecting connection: {conn} because reading a message of type {typeof(T)} caused an Exception. This can happen if the other side accidentally (or an attacker intentionally) sent invalid data. Reason: {exception}");
                        conn.Disconnect();
                        return;
                    }
                    // otherwise log it but allow the connection to keep playing
                    else
                    {
                        Debug.LogError($"Caught an Exception when reading a message from: {conn} of type {typeof(T)}. Reason: {exception}");
                        return;
                    }
                }
                finally
                {
                    int endPos = reader.Position;
                    // TODO: Figure out the correct channel
                    //NetworkDiagnostics.OnReceive(message, channelId, endPos - startPos);
                }

                // user handler exception should not stop the whole server
                try
                {
                    // user implemented handler
                    handler((C)conn, message, channeldId);
                }
                catch(Exception exception)
                {
                    // should we disconnect on exceptions?
                    if (exceptionsDisconnect)
                    {
                        Debug.LogError($"Disconnecting connection: {conn} because handling a message of type {typeof(T)} caused an Exception. This can happen if the other side accidentally (or an attacker intentionally) sent invalid data. Reason: {exception}");
                        conn.Disconnect();
                    }
                    // otherwise log it but allow the connection to keep playing
                    else
                    {
                        Debug.LogError($"Caught an Exception when handling a message from: {conn} of type {typeof(T)}. Reason: {exception}");
                    }
                }
            };
    }
}
