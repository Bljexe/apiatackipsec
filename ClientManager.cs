using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NLog;
using Stump.Core.Attributes;
using Stump.Core.Collections;
using Stump.Core.Pool;
using Stump.Core.Reflection;

namespace Stump.Server.BaseServer.Network {
    public class ClientManager : Singleton<ClientManager> {
        private readonly Logger logger = LogManager.GetCurrentClassLogger ();

        #region Config Variables
        /// <summary>
        /// Max number of clients connected
        /// </summary>
        [Variable]
        public static int MaxConcurrentConnections = 2000;

        /// <summary>
        /// Max number of clients waiting for a connection
        /// </summary>
        [Variable]
        public static int MaxPendingConnections = 100;

        /// <summary>
        /// Max number of clients connected on the same IP or NULL for unlimited
        /// </summary>
        [Variable]
        public static int? MaxIPConnexions = 10;

        /// <summary>
        /// Buffer size /!\ Advanced users only /!\
        /// </summary>
        [Variable]
        public static int BufferSize = 8192;
        #endregion

        #region Events

        public event Action<BaseClient> ClientConnected;

        private void NotifyClientConnected (BaseClient client) {
            var handler = ClientConnected;
            if (handler != null) handler (client);
        }

        public event Action<BaseClient> ClientDisconnected;

        private void NotifyClientDisconnected (BaseClient client) {
            var handler = ClientDisconnected;
            if (handler != null) handler (client);
        }

        #endregion

        public delegate BaseClient CreateClientHandler (Socket clientSocket);

        private CreateClientHandler m_createClientDelegate;

        private readonly Socket m_listenSocket = new Socket (AddressFamily.InterNetwork, SocketType.Stream,
            ProtocolType.Tcp);

        private readonly List<BaseClient> m_clients = new List<BaseClient> ();

        private readonly SocketAsyncEventArgs m_acceptArgs = new SocketAsyncEventArgs (); // async arg used on client connection
        private SemaphoreSlim m_semaphore; // limit the number of threads accessing to a ressource

        private readonly AutoResetEvent m_resumeEvent = new AutoResetEvent (false);

        private static LimitedStack<Pair<DateTime, string>> m_IpErrorHistory = new LimitedStack<Pair<DateTime, string>>(500);//500 ips error in memory
        private static List<string> CheckList = new List<string>();
        private static List<string> BlockList = new List<string>();
        private static List<string> AuthList = new List<string>();
        private static readonly object m_locker = new object();
        private static readonly object m_locker_check = new object();

        /// <summary>
        /// List of connected Clients
        /// </summary>
        public ReadOnlyCollection<BaseClient> Clients {
            get { return m_clients.AsReadOnly (); }
        }

        public int Count {
            get {
                return m_clients.Count;
            }
        }

        public bool Paused {
            get;
            private set;
        }

        public bool IsInitialized {
            get;
            private set;
        }

        public bool Started {
            get;
            private set;
        }

        public string Host {
            get;
            private set;
        }

        public int Port {
            get;
            private set;
        }

        public bool IsFull {
            get;
            private set;
        }

        public void Initialize (CreateClientHandler createClientHandler) {
            if (IsInitialized)
                throw new Exception ("ClientManager already initialized");

            if (!ObjectPoolMgr.ContainsType<PoolableSocketArgs> ()) {
                ObjectPoolMgr.RegisterType (() => new PoolableSocketArgs ());
                ObjectPoolMgr.SetMinimumSize<PoolableSocketArgs> (MaxConcurrentConnections);
            }

            m_createClientDelegate = createClientHandler;

            // init semaphore
            m_semaphore = new SemaphoreSlim (MaxConcurrentConnections, MaxConcurrentConnections);
            m_acceptArgs.Completed += (sender, e) => ProcessAccept (e);

            IsInitialized = true;
        }

        /// <summary>
        /// Start to listen client connections
        /// </summary>
        public void Start (string address, int port) {
            if (!IsInitialized)
                throw new Exception ("Attempt to start ClientManager before initializing it. Call Initialize()");

            if (Started)
                throw new Exception ("ClientManager already started");

            Host = address;
            Port = port;

            var ipEndPoint = new IPEndPoint (Dns.GetHostAddresses (Host).First (ip => ip.AddressFamily == AddressFamily.InterNetwork), Port);
            m_listenSocket.NoDelay = true;
            m_listenSocket.Bind (ipEndPoint);
            m_listenSocket.Listen (MaxPendingConnections);

            Started = true;

            StartAccept ();
        }

        /// <summary>
        /// Pause the listener and reject all new connections
        /// </summary>
        public void Pause () {
            Paused = true;
        }

        /// <summary>
        /// Resume the actual pause
        /// </summary>
        public void Resume () {
            Paused = false;

            m_resumeEvent.Set ();
        }

        /// <summary>
        /// Close the listener and dispose ressources
        /// </summary>
        public void Close () {
            // interrupt accept process
            Paused = true;

            m_listenSocket.Close ();
            m_listenSocket.Dispose ();
        }

        private void StartAccept () {
            m_acceptArgs.AcceptSocket = null;

            if (m_semaphore.CurrentCount == 0) {
                logger.Warn ("Connected clients limits reached ! ({0}) Waiting for a disconnection ...", Count);
                IsFull = true;
            }

            // thread block if the max connections limit is reached
            m_semaphore.Wait ();

            if (IsFull) {
                IsFull = false;
                logger.Warn ("A client get disconnected, connection allowed", m_semaphore.CurrentCount);
            }

            // raise or not the event depending on AcceptAsync return
            if (!m_listenSocket.AcceptAsync (m_acceptArgs)) {
                ProcessAccept (m_acceptArgs);
            }
        }

        /// <summary>
        /// Called when a new client is connecting
        /// </summary>
        /// <param name="e"></param>
        private void ProcessAccept (SocketAsyncEventArgs e) {
            BaseClient client = null;
            try {
                // do not accept connections while pausing
                if (Paused) {
                    logger.Warn ("Pause state. Connection pending ...", m_semaphore.CurrentCount);
                    // if paused wait until Resume() is called
                    m_resumeEvent.WaitOne ();
                }

                try {
                    if (e.AcceptSocket.RemoteEndPoint == null) {
                        logger.Error ("Invalid remote end-point (null)");

                        m_semaphore.Release ();
                        return;
                    }

                    var IP = ((IPEndPoint) e.AcceptSocket.RemoteEndPoint).Address;
                    if (!CheckAttack(IP.ToString()))
                    {
                        logger.Error("Client {0} Blocked per CheckAttack!",
                            e.AcceptSocket.RemoteEndPoint.ToString());
                        m_semaphore.Release();
                        return;
                    }

                    if (MaxIPConnexions.HasValue && CountClientWithSameIp (IP) > MaxIPConnexions.Value) {
                        logger.Error ("Client {0} try to connect more then {1} times",
                            e.AcceptSocket.RemoteEndPoint.ToString (), MaxIPConnexions.Value);
                        m_semaphore.Release ();
                        return;
                    }
                } catch (Exception ex) {
                    logger.Error ("Invalid remote end-point {1}. Exception : {0}", ex, e.AcceptSocket.RemoteEndPoint);
                    m_semaphore.Release ();
                    return;
                }

                // use a async arg from the pool avoid to re-allocate memory on each connection

                // create the client instance
                client = m_createClientDelegate (e.AcceptSocket);

                lock (m_clients)
                m_clients.Add (client);

                NotifyClientConnected (client);

                client.BeginReceive ();
            } catch (Exception ex) {
                // if an error occurs we do our possible to reset all possible allocated ressources
                logger.Error ("Cannot accept a connection from {0}. Exception : {1}", e.RemoteEndPoint, ex);

                if (client != null)
                    OnClientDisconnected (client);
                else {
                    m_semaphore.Release ();

                    if (e.AcceptSocket != null) {
                        if (e.AcceptSocket.Connected)
                            e.AcceptSocket.Disconnect (false);
                    }
                }
            } finally {
                StartAccept ();
            }
        }

        public void OnClientDisconnected (BaseClient client) {
            bool removed;
            lock (m_clients)
            removed = m_clients.Remove (client);

            if (!removed)
                return;

            NotifyClientDisconnected (client);

            m_semaphore.Release ();
        }

        public PoolableSocketArgs PopSocketArg () {
            var arg = ObjectPoolMgr.ObtainObject<PoolableSocketArgs> ();
            return arg;
        }

        public void PushSocketArg (PoolableSocketArgs args) {
            ObjectPoolMgr.ReleaseObject (args);
        }

        public BaseClient[] FindAll (Predicate<BaseClient> predicate) {
            lock (m_clients) {
                return m_clients.Where (entry => predicate (entry)).ToArray ();
            }
        }

        public T[] FindAll<T> (Predicate<T> predicate) {
            lock (m_clients) {
                return m_clients.OfType<T> ().Where (entry => predicate (entry)).ToArray ();
            }
        }

        public T[] FindAll<T> () {
            lock (m_clients) {
                return m_clients.OfType<T> ().ToArray ();
            }
        }

        public int CountClientWithSameIp (IPAddress ipAddress) {
            lock (m_clients) {
                var count = 0;
                foreach (var t in m_clients) {
                    if (t.Socket != null && t.Socket.Connected && t.Socket.RemoteEndPoint != null) {
                        var ip = ((IPEndPoint) t.Socket.RemoteEndPoint).Address;
                        if (ip.Equals (ipAddress) && ip.AddressFamily != AddressFamily.InterNetwork) // not a fake client
                            count++;
                    }
                }

                return count;
            }
        }
        public bool CheckAttack(string ipAddress)
        {
            lock (m_locker_check)
            {
                if (CheckList.Contains(ipAddress) || BlockList.Contains(ipAddress))
                    return false;
                if (AuthList.Contains(ipAddress))
                    return true;
                CheckList.Add(ipAddress);
            }
            string[] jsonExploded = null;
            try
            {
                WebClient web = new WebClient();
                string json;
                json = web.DownloadString("https://api.blocklist.de/api.php?ip=" + ipAddress);
                if (json == null)//site off?!
                {
                    lock (m_locker_check)
                    {
                        CheckList.Remove(ipAddress);
                        return true;
                    }
                }
                json = json.Replace("attacks: ", "").Replace("<br />reports: ", ",").Replace("<br />", "");
                Console.WriteLine(jsonExploded);
                jsonExploded = json.Split(',');
            }
            catch
            {
                lock (m_locker_check)
                {
                    CheckList.Remove(ipAddress);
                    return true;
                }
            }
            int attacks = 0;
            int report = 0;
            int.TryParse(jsonExploded[0], out attacks);
            int.TryParse(jsonExploded[1], out report);

            if (attacks > 0 || report > 0)
            {
                AddErrorIP(ipAddress, true);
                lock (m_locker_check)
                {
                    BlockList.Add(ipAddress);
                    CheckList.Remove(ipAddress);
                    return false;
                }
            }
            else
            {
                lock (m_locker_check)
                {
                    AuthList.Add(ipAddress);
                    CheckList.Remove(ipAddress);

                    return true;
                }
            }
        }

        public static void AddErrorIP(string m_ip, bool force = false)
        {
            lock (m_locker)
            {
                if (m_ip == null || m_ip == "")
                    return;
                if (!force)
                    m_IpErrorHistory.Push(new Pair<DateTime, string>(DateTime.Now, m_ip));
                if (m_IpErrorHistory.Where(x => x.Second == m_ip && DateTime.Now.Subtract(x.First).TotalSeconds < 3.0).Count() >= 5 || force)
                {
                    if (force)
                    {
                        lock (m_locker_check)
                            BlockList.Add(m_ip);
                    }
                    Process process = new Process();
                    process.StartInfo.FileName = "cmd.exe";
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardInput = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.UseShellExecute = false;
                    process.Start();
                    string code = "netsh advfirewall firewall add rule name=\"IP Block\" dir=in interface=any action=block remoteip=" + m_ip + "/32";
                    process.StandardInput.WriteLine(code);
                    process.StandardInput.Flush();
                    process.StandardInput.Close();
                }
            }

        }
    }
}