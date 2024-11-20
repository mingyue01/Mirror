using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Mirror;
using UnityEngine;

namespace Network
{
    public static partial class NetworkServer
    {
        static bool initialized;
        public static int maxConnections;

        /// <summary>Server Update frequency, per second. Use around 60Hz for fast paced games like Counter-Strike to minimize latency. Use around 30Hz for games like WoW to minimize computations. Use around 1-10Hz for slow paced games like EVE.</summary>
        // overwritten by NetworkManager (if any)
        public static int tickRate = 60;

        // tick rate is in Hz.
        // convert to interval in seconds for convenience where needed.
        //
        // send interval is 1 / sendRate.
        // but for tests we need a way to set it to exactly 0.
        // 1 / int.max would not be exactly 0, so handel that manually.
        public static float tickInterval => tickRate < int.MaxValue ? 1f / tickRate : 0; // for 30 Hz, that's 33ms

        // time & value snapshot interpolation are separate.
        // -> time is interpolated globally on NetworkClient / NetworkConnection
        // -> value is interpolated per-component, i.e. NetworkTransform.
        // however, both need to be on the same send interval.
        public static int sendRate => tickRate;
        public static float sendInterval => sendRate < int.MaxValue ? 1f / sendRate : 0; // for 30 Hz, that's 33ms
        static double lastSendTime;

        /// <summary>Dictionary of all server connections, with connectionId as key</summary>
        public static Dictionary<int, NetworkConnectionToClient> connections =
            new Dictionary<int, NetworkConnectionToClient>();

        /// <summary>Message Handlers dictionary, with messageId as key</summary>
        internal static Dictionary<ushort, NetworkMessageDelegate> handlers =
            new Dictionary<ushort, NetworkMessageDelegate>();

        /// <summary>Single player mode can set listen=false to not accept incoming connections.</summary>
        public static bool listen;

        /// <summary>active checks if the server has been started either has standalone or as host server.</summary>
        public static bool active { get; internal set; }

        // scene loading
        public static bool isLoadingScene;

        // interest management component (optional)
        // by default, everyone observes everyone
        public static InterestManagementBase aoi;

        // For security, it is recommended to disconnect a player if a networked
        // action triggers an exception\nThis could prevent components being
        // accessed in an undefined state, which may be an attack vector for
        // exploits.
        //
        // However, some games may want to allow exceptions in order to not
        // interrupt the player's experience.
        public static bool exceptionsDisconnect = true; // security by default

        // Mirror global disconnect inactive option, independent of Transport.
        // not all Transports do this properly, and it's easiest to configure this just once.
        // this is very useful for some projects, keep it.
        public static bool disconnectInactiveConnections;
        public static float disconnectInactiveTimeout = 60;

        // OnConnected / OnDisconnected used to be NetworkMessages that were
        // invoked. this introduced a bug where external clients could send
        // Connected/Disconnected messages over the network causing undefined
        // behaviour.
        // => public so that custom NetworkManagers can hook into it
        public static Action<NetworkConnectionToClient> OnConnectedEvent;
        public static Action<NetworkConnectionToClient> OnDisconnectedEvent;
        public static Action<NetworkConnectionToClient, TransportError, string> OnErrorEvent;
        public static Action<NetworkConnectionToClient, Exception> OnTransportExceptionEvent;

        // keep track of actual achieved tick rate.
        // might become lower under heavy load.
        // very useful for profiling etc.
        // measured over 1s each, same as frame rate. no EMA here.
        public static int actualTickRate;
        static double actualTickRateStart;   // start time when counting
        static int actualTickRateCounter; // current counter since start

        // profiling
        // includes transport update time, because transport calls handlers etc.
        // averaged over 1s by passing 'tickRate' to constructor.
        public static TimeSample earlyUpdateDuration;
        public static TimeSample lateUpdateDuration;

        // capture full Unity update time from before Early- to after LateUpdate
        public static TimeSample fullUpdateDuration;

        internal static readonly List<NetworkConnectionToClient> connectionsCopy =
    new List<NetworkConnectionToClient>();

        /// <summary>Starts server and listens to incoming connections with max connections limit.</summary>
        public static void Listen(int maxConns)
        {
            Initialize();
            maxConnections = maxConns;

            // only start server if we want to listen
            if (listen)
            {
                Transport.active.ServerStart();

                if (Transport.active is PortTransport portTransport)
                {
                    if (Utils.IsHeadless())
                    {
#if !UNITY_EDITOR
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"Server listening on port {portTransport.Port}");
                        Console.ResetColor();
#else
                        Debug.Log($"Server listening on port {portTransport.Port}");
#endif
                    }
                }
                else
                    Debug.Log("Server started listening");
            }

            active = true;
            RegisterMessageHandlers();
        }

        // initialization / shutdown ///////////////////////////////////////////
        static void Initialize()
        {
            if (initialized)
                return;

            // safety: ensure Weaving succeded.
            // if it silently failed, we would get lots of 'writer not found'
            // and other random errors at runtime instead. this is cleaner.
            if (!WeaverFuse.Weaved())
            {
                // if it failed, throw an exception to early exit all Listen calls.
                throw new Exception("NetworkServer won't start because Weaving failed or didn't run.");
            }

            // Debug.Log($"NetworkServer Created version {Version.Current}");

            //Make sure connections are cleared in case any old connections references exist from previous sessions
            connections.Clear();

            // reset Interest Management so that rebuild intervals
            // start at 0 when starting again.
            if (aoi != null) aoi.ResetState();

            // reset NetworkTime
            NetworkTime.ResetStatics();

            Debug.Assert(Transport.active != null, "There was no active transport when calling NetworkServer.Listen, If you are calling Listen manually then make sure to set 'Transport.active' first");
            AddTransportHandlers();

            initialized = true;

            // profiling
            earlyUpdateDuration = new TimeSample(sendRate);
            lateUpdateDuration = new TimeSample(sendRate);
            fullUpdateDuration = new TimeSample(sendRate);
        }

        static void AddTransportHandlers()
        {
            // += so that other systems can also hook into it (i.e. statistics)
//#pragma warning disable CS0618 // Type or member is obsolete
//            Transport.active.OnServerConnected += OnTransportConnected;
//#pragma warning restore CS0618 // Type or member is obsolete
            Transport.active.OnServerConnectedWithAddress += OnTransportConnectedWithAddress;
            Transport.active.OnServerDataReceived += OnTransportData;
            Transport.active.OnServerDisconnected += OnTransportDisconnected;
            Transport.active.OnServerError += OnTransportError;
            Transport.active.OnServerTransportException += OnTransportException;
        }

        /// <summary>Shuts down the server and disconnects all clients</summary>
        // RuntimeInitializeOnLoadMethod -> fast playmode without domain reload
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Shutdown()
        {
            if (initialized)
            {
                DisconnectAll();

                // stop the server.
                // we do NOT call Transport.Shutdown, because someone only
                // called NetworkServer.Shutdown. we can't assume that the
                // client is supposed to be shut down too!
                //
                // NOTE: stop no matter what, even if 'dontListen':
                //       someone might enabled dontListen at runtime.
                //       but we still need to stop the server.
                //       fixes https://github.com/vis2k/Mirror/issues/2536
                Transport.active.ServerStop();

                // transport handlers are hooked into when initializing.
                // so only remove them when shutting down.
                RemoveTransportHandlers();

                initialized = false;
            }

            // Reset all statics here....
            listen = true;
            isLoadingScene = false;
            lastSendTime = 0;
            actualTickRate = 0;

            //localConnection = null;

            connections.Clear();
            connectionsCopy.Clear();
            handlers.Clear();

            // destroy all spawned objects, _then_ set inactive.
            // make sure .active is still true before calling this.
            // otherwise modifying SyncLists in OnStopServer would throw
            // because .IsWritable() check checks if NetworkServer.active.
            // https://github.com/MirrorNetworking/Mirror/issues/3344
            //CleanupSpawned();
            active = false;

            // sets nextNetworkId to 1
            // sets clientAuthorityCallback to null
            // sets previousLocalPlayer to null
            //todo
            //NetworkIdentity.ResetStatics();

            // clear events. someone might have hooked into them before, but
            // we don't want to use those hooks after Shutdown anymore.
            OnConnectedEvent = null;
            OnDisconnectedEvent = null;
            OnErrorEvent = null;
            OnTransportExceptionEvent = null;

            if (aoi != null) aoi.ResetState();
        }

        static bool IsConnectionAllowed(int connectionId, string address)
        {
            // only accept connections while listening
            if (!listen)
            {
                Debug.Log($"Server not listening, rejecting connectionId={connectionId} with address={address}");
                return false;
            }

            // connectionId needs to be != 0 because 0 is reserved for local player
            // note that some transports like kcp generate connectionId by
            // hashing which can be < 0 as well, so we need to allow < 0!
            if (connectionId == 0)
            {
                Debug.LogError($"Server.HandleConnect: invalid connectionId={connectionId}. Needs to be != 0, because 0 is reserved for local player.");
                return false;
            }

            // connectionId not in use yet?
            if (connections.ContainsKey(connectionId))
            {
                Debug.LogError($"Server connectionId={connectionId} already in use. Client with address={address} will be kicked");
                return false;
            }

            // are more connections allowed? if not, kick
            // (it's easier to handle this in Mirror, so Transports can have
            //  less code and third party transport might not do that anyway)
            // (this way we could also send a custom 'tooFull' message later,
            //  Transport can't do that)
            if (connections.Count >= maxConnections)
            {
                Debug.LogError($"Server full, client connectionId={connectionId} with address={address} will be kicked");
                return false;
            }

            return true;
        }

        public static bool AddConnection(NetworkConnectionToClient conn)
        {
            if (!connections.ContainsKey(conn.connectionId))
            {
                // connection cannot be null here or conn.connectionId
                // would throw NRE
                connections[conn.connectionId] = conn;
                return true;
            }
            // already a connection with this id
            return false;
        }

        /// <summary>Removes a connection by connectionId. Returns true if removed.</summary>
        public static bool RemoveConnection(int connectionId) =>
            connections.Remove(connectionId);

        internal static void OnConnected(NetworkConnectionToClient conn)
        {
            // Debug.Log($"Server accepted client:{conn}");

            // add connection and invoke connected event
            AddConnection(conn);
            OnConnectedEvent?.Invoke(conn);
        }

        static void OnTransportConnectedWithAddress(int connectionId, string clientAddress)
        {
            if (IsConnectionAllowed(connectionId, clientAddress))
            {
                // create a connection
                NetworkConnectionToClient conn = new NetworkConnectionToClient(connectionId, clientAddress);
                OnConnected(conn);
            }
            else
            {
                // kick the client immediately
                Transport.active.ServerDisconnect(connectionId);
            }
        }

        static bool UnpackAndInvoke(NetworkConnectionToClient connection, NetworkReader reader, int channelId)
        {
            if (NetworkMessages.UnpackId(reader, out ushort msgType))
            {
                // try to invoke the handler for that message
                if (handlers.TryGetValue(msgType, out NetworkMessageDelegate handler))
                {
                    //todo
                    handler.Invoke(connection, reader, channelId);
                    connection.lastMessageTime = Time.time;
                    return true;
                }
                else
                {
                    // message in a batch are NOT length prefixed to save bandwidth.
                    // every message needs to be handled and read until the end.
                    // otherwise it would overlap into the next message.
                    // => need to warn and disconnect to avoid undefined behaviour.
                    // => WARNING, not error. can happen if attacker sends random data.
                    Debug.LogWarning($"Unknown message id: {msgType} for connection: {connection}. This can happen if no handler was registered for this message.");
                    // simply return false. caller is responsible for disconnecting.
                    //connection.Disconnect();
                    return false;
                }
            }
            else
            {
                // => WARNING, not error. can happen if attacker sends random data.
                Debug.LogWarning($"Invalid message header for connection: {connection}.");
                // simply return false. caller is responsible for disconnecting.
                //connection.Disconnect();
                return false;
            }
        }

        internal static void OnTransportData(int connectionId, ArraySegment<byte> data, int channelId)
        {
            if (connections.TryGetValue(connectionId, out NetworkConnectionToClient connection))
            {
                // client might batch multiple messages into one packet.
                // feed it to the Unbatcher.
                // NOTE: we don't need to associate a channelId because we
                //       always process all messages in the batch.
                if (!connection.unbatcher.AddBatch(data))
                {
                    if (exceptionsDisconnect)
                    {
                        Debug.LogError($"NetworkServer: received message from connectionId:{connectionId} was too short (messages should start with message id). Disconnecting.");
                        connection.Disconnect();
                    }
                    else
                        Debug.LogWarning($"NetworkServer: received message from connectionId:{connectionId} was too short (messages should start with message id).");

                    return;
                }

                // process all messages in the batch.
                // only while NOT loading a scene.
                // if we get a scene change message, then we need to stop
                // processing. otherwise we might apply them to the old scene.
                // => fixes https://github.com/vis2k/Mirror/issues/2651
                //
                // NOTE: if scene starts loading, then the rest of the batch
                //       would only be processed when OnTransportData is called
                //       the next time.
                //       => consider moving processing to NetworkEarlyUpdate.
                while (!isLoadingScene &&
                       connection.unbatcher.GetNextMessage(out ArraySegment<byte> message, out double remoteTimestamp))
                {
                    using (NetworkReaderPooled reader = NetworkReaderPool.Get(message))
                    {
                        // enough to read at least header size?
                        if (reader.Remaining >= NetworkMessages.IdSize)
                        {
                            // make remoteTimeStamp available to the user
                            connection.remoteTimeStamp = remoteTimestamp;

                            // handle message
                            if (!UnpackAndInvoke(connection, reader, channelId))
                            {
                                // warn, disconnect and return if failed
                                // -> warning because attackers might send random data
                                // -> messages in a batch aren't length prefixed.
                                //    failing to read one would cause undefined
                                //    behaviour for every message afterwards.
                                //    so we need to disconnect.
                                // -> return to avoid the below unbatches.count error.
                                //    we already disconnected and handled it.
                                if (exceptionsDisconnect)
                                {
                                    Debug.LogError($"NetworkServer: failed to unpack and invoke message. Disconnecting {connectionId}.");
                                    connection.Disconnect();
                                }
                                else
                                    Debug.LogWarning($"NetworkServer: failed to unpack and invoke message from connectionId:{connectionId}.");

                                return;
                            }
                        }
                        // otherwise disconnect
                        else
                        {
                            if (exceptionsDisconnect)
                            {
                                Debug.LogError($"NetworkServer: received message from connectionId:{connectionId} was too short (messages should start with message id). Disconnecting.");
                                connection.Disconnect();
                            }
                            else
                                Debug.LogWarning($"NetworkServer: received message from connectionId:{connectionId} was too short (messages should start with message id).");

                            return;
                        }
                    }
                }

                // if we weren't interrupted by a scene change,
                // then all batched messages should have been processed now.
                // otherwise batches would silently grow.
                // we need to log an error to avoid debugging hell.
                //
                // EXAMPLE: https://github.com/vis2k/Mirror/issues/2882
                // -> UnpackAndInvoke silently returned because no handler for id
                // -> Reader would never be read past the end
                // -> Batch would never be retired because end is never reached
                //
                // NOTE: prefixing every message in a batch with a length would
                //       avoid ever not reading to the end. for extra bandwidth.
                //
                // IMPORTANT: always keep this check to detect memory leaks.
                //            this took half a day to debug last time.
                if (!isLoadingScene && connection.unbatcher.BatchesCount > 0)
                {
                    Debug.LogError($"Still had {connection.unbatcher.BatchesCount} batches remaining after processing, even though processing was not interrupted by a scene change. This should never happen, as it would cause ever growing batches.\nPossible reasons:\n* A message didn't deserialize as much as it serialized\n*There was no message handler for a message id, so the reader wasn't read until the end.");
                }
            }
            else Debug.LogError($"HandleData Unknown connectionId:{connectionId}");
        }

        // called by transport
        // IMPORTANT: often times when disconnecting, we call this from Mirror
        //            too because we want to remove the connection and handle
        //            the disconnect immediately.
        //            => which is fine as long as we guarantee it only runs once
        //            => which we do by removing the connection!
        internal static void OnTransportDisconnected(int connectionId)
        {
            // Debug.Log($"Server disconnect client:{connectionId}");
            if (connections.TryGetValue(connectionId, out NetworkConnectionToClient conn))
            {
                conn.Cleanup();
                RemoveConnection(connectionId);
                // Debug.Log($"Server lost client:{connectionId}");

                // NetworkManager hooks into OnDisconnectedEvent to make
                // DestroyPlayerForConnection(conn) optional, e.g. for PvP MMOs
                // where players shouldn't be able to escape combat instantly.
                if (OnDisconnectedEvent != null)
                {
                    OnDisconnectedEvent.Invoke(conn);
                }
                // if nobody hooked into it, then simply call DestroyPlayerForConnection
                //else
                //{
                //    DestroyPlayerForConnection(conn);
                //}
            }
        }

        // transport errors are forwarded to high level
        static void OnTransportError(int connectionId, TransportError error, string reason)
        {
            // transport errors will happen. logging a warning is enough.
            // make sure the user does not panic.
            Debug.LogWarning($"Server Transport Error for connId={connectionId}: {error}: {reason}. This is fine.");
            // try get connection. passes null otherwise.
            connections.TryGetValue(connectionId, out NetworkConnectionToClient conn);
            OnErrorEvent?.Invoke(conn, error, reason);
        }

        // transport errors are forwarded to high level
        static void OnTransportException(int connectionId, Exception exception)
        {
            // transport errors will happen. logging a warning is enough.
            // make sure the user does not panic.
            Debug.LogWarning($"Server Transport Exception for connId={connectionId}: {exception}");
            // try get connection. passes null otherwise.
            connections.TryGetValue(connectionId, out NetworkConnectionToClient conn);
            OnTransportExceptionEvent?.Invoke(conn, exception);
        }

        internal static void RegisterMessageHandlers()
        {
            //todo: implement
            throw new NotImplementedException();
        }

        static void RemoveTransportHandlers()
        {
            // -= so that other systems can also hook into it (i.e. statistics)
#pragma warning disable CS0618 // Type or member is obsolete
            //Transport.active.OnServerConnected -= OnTransportConnected;
#pragma warning restore CS0618 // Type or member is obsolete
            Transport.active.OnServerConnectedWithAddress -= OnTransportConnectedWithAddress;
            Transport.active.OnServerDataReceived -= OnTransportData;
            Transport.active.OnServerDisconnected -= OnTransportDisconnected;
            Transport.active.OnServerError -= OnTransportError;
        }

        // disconnect //////////////////////////////////////////////////////////
        /// <summary>Disconnect all connections, including the local connection.</summary>
        // synchronous: handles disconnect events and cleans up fully before returning!
        public static void DisconnectAll()
        {
            // disconnect and remove all connections.
            // we can not use foreach here because if
            //   conn.Disconnect -> Transport.ServerDisconnect calls
            //   OnDisconnect -> NetworkServer.OnDisconnect(connectionId)
            // immediately then OnDisconnect would remove the connection while
            // we are iterating here.
            //   see also: https://github.com/vis2k/Mirror/issues/2357
            // this whole process should be simplified some day.
            // until then, let's copy .Values to avoid InvalidOperationException.
            // note that this is only called when stopping the server, so the
            // copy is no performance problem.
            foreach (NetworkConnectionToClient conn in connections.Values.ToList())
            {
                // disconnect via connection->transport
                conn.Disconnect();

                // we want this function to be synchronous: handle disconnect
                // events and clean up fully before returning.
                // -> OnTransportDisconnected can safely be called without
                //    waiting for the Transport's callback.
                // -> it has checks to only run once.

                // call OnDisconnected unless local player in host mod
                // TODO unnecessary check?
                //if (conn.connectionId != NetworkConnection.LocalConnectionId)
                    OnTransportDisconnected(conn.connectionId);
            }

            // cleanup
            connections.Clear();
            //localConnection = null;
            // this used to set active=false.
            // however, then Shutdown can't properly destroy objects:
            // https://github.com/MirrorNetworking/Mirror/issues/3344
            // "DisconnectAll" should only disconnect all, not set inactive.
            // active = false;
        }


        // send ////////////////////////////////////////////////////////////////
        /// <summary>Send a message to all clients, even those that haven't joined the world yet (non ready)</summary>
        public static void SendToAll<T>(ushort messageId, T message, int channelId = Channels.Reliable, bool sendToReadyOnly = false)
            where T : IMessage
        {
            if (!active)
            {
                Debug.LogWarning("Can not send using NetworkServer.SendToAll<T>(T msg) because NetworkServer is not active");
                return;
            }

            // Debug.Log($"Server.SendToAll {typeof(T)}");
            using (NetworkWriterPooled writer = NetworkWriterPool.Get())
            {
                // pack message only once
                NetworkMessages.Pack(messageId, message, writer);
                ArraySegment<byte> segment = writer.ToArraySegment();

                // validate packet size immediately.
                // we know how much can fit into one batch at max.
                // if it's larger, log an error immediately with the type <T>.
                // previously we only logged in Update() when processing batches,
                // but there we don't have type information anymore.
                int max = NetworkMessages.MaxMessageSize(channelId);
                if (writer.Position > max)
                {
                    Debug.LogError($"NetworkServer.SendToAll: message of type {typeof(T)} with a size of {writer.Position} bytes is larger than the max allowed message size in one batch: {max}.\nThe message was dropped, please make it smaller.");
                    return;
                }

                // filter and then send to all internet connections at once
                // -> makes code more complicated, but is HIGHLY worth it to
                //    avoid allocations, allow for multicast, etc.
                int count = 0;
                foreach (NetworkConnectionToClient conn in connections.Values)
                {
                    if (sendToReadyOnly && !conn.isReady)
                        continue;

                    count++;
                    conn.Send(segment, channelId);
                }

                //NetworkDiagnostics.OnSend(message, channelId, segment.Count, count);
            }
        }

        /// <summary>Register a handler for message type T. Most should require authentication.</summary>
        // This version passes channelId to the handler.
        public static void RegisterHandler<T>(Action<NetworkConnectionToClient, T, int> handler, ushort messageId, bool requireAuthentication = true)
            where T : IMessage
        {
            ushort msgType = messageId;
            if (handlers.ContainsKey(msgType))
            {
                Debug.LogWarning($"NetworkServer.RegisterHandler replacing handler for {typeof(T).FullName}, id={msgType}. If replacement is intentional, use ReplaceHandler instead to avoid this warning.");
            }

            // register Id <> Type in lookup for debugging.
            NetworkMessages.Lookup[msgType] = typeof(T);

            handlers[msgType] = NetworkMessages.WrapHandler(handler, messageId, requireAuthentication, exceptionsDisconnect);
        }
    }
}
