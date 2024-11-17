using System;
using System.Collections.Generic;
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
            //todo
            //Transport.active.OnServerDisconnected += OnTransportDisconnected;
            //Transport.active.OnServerError += OnTransportError;
            //Transport.active.OnServerTransportException += OnTransportException;
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
                    //handler.Invoke(connection, reader, channelId);
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

        internal static void RegisterMessageHandlers()
        {
            //todo: implement
            throw new NotImplementedException();
        }
    }
}
