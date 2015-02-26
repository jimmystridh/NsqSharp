﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NsqSharp.Channels;
using NsqSharp.Extensions;
using NsqSharp.Go;
using NsqSharp.Utils;
using Timer = NsqSharp.Go.Timer;

namespace NsqSharp
{
    /// <summary>
    /// IHandler is the message processing interface for <see cref="Consumer" />
    ///
    /// Implement this interface for handlers that return whether or not message
    /// processing completed successfully.
    /// 
    /// When the return value is nil Consumer will automatically handle FINishing.
    ///
    /// When the returned value is non-nil Consumer will automatically handle REQueing.
    /// </summary>
    public interface IHandler
    {
        /// <summary>
        /// Handles messages.
        /// </summary>
        /// <param name="message">The message.</param>
        void HandleMessage(Message message);

        /// <summary>
        /// Called when a message is deemed "failed" (i.e. the number of attempts
        /// exceeded the Consumer specified MaxAttemptCount)
        /// </summary>
        /// <param name="message">The failed message.</param>
        void LogFailedMessage(Message message);
    }

    /// <summary>
    /// DiscoveryFilter is an interface accepted by `SetBehaviorDelegate()`
    /// for filtering the nsqds returned from discovery via nsqlookupd
    /// </summary>
    public interface IDiscoveryFilter
    {
        /// <summary>
        /// Filters a list of NSQD addresses.
        /// </summary>
        Collection<string> Filter(IEnumerable<string> nsqds);
    }

    /// <summary>
    /// ConsumerStats represents a snapshot of the state of a Consumer's connections and the messages
    /// it has seen
    /// </summary>
    public class ConsumerStats
    {
        /// <summary>Messages Received</summary>
        public long MessagesReceived { get; set; }
        /// <summary>Messages Finished</summary>
        public long MessagesFinished { get; set; }
        /// <summary>Messages Requeued</summary>
        public long MessagesRequeued { get; set; }
        /// <summary>Connections</summary>
        public int Connections { get; set; }
    }

    /// <summary>
    /// Consumer is a high-level type to consume from NSQ.
    ///
    /// A Consumer instance is supplied a Handler that will be executed
    /// concurrently via goroutines to handle processing the stream of messages
    /// consumed from the specified topic/channel. See: Handler/HandlerFunc
    /// for details on implementing the interface to create handlers.
    ///
    /// If configured, it will poll nsqlookupd instances and handle connection (and
    /// reconnection) to any discovered nsqds.
    /// </summary>
    public class Consumer
    {
        private static readonly byte[] CLOSE_WAIT_BYTES = Encoding.UTF8.GetBytes("CLOSE_WAIT");

        private static long _instCount;

        private long _messagesReceived;
        private long _messagesFinished;
        private long _messagesRequeued;
        private long _totalRdyCount;
        private long _backoffDuration;
        private int _maxInFlight;

        private readonly ReaderWriterLockSlim _mtx = new ReaderWriterLockSlim();

        private ILogger _logger;
        private LogLevel _logLvl;

        private IDiscoveryFilter _behaviorDelegate;

        private readonly long _id;
        private readonly string _topic;
        private readonly string _channel;
        private readonly Config _config;

        private readonly RNGCryptoServiceProvider _rng; // TODO: Dispose, or make static

        private int _needRDYRedistributed;

        private readonly ReaderWriterLockSlim _backoffMtx = new ReaderWriterLockSlim(); // TODO: Dispose
        private int _backoffCounter;

        private readonly Chan<Message> _incomingMessages;

        private readonly ReaderWriterLockSlim _rdyRetryMtx = new ReaderWriterLockSlim(); // TODO: Dispose
        private readonly Dictionary<string, Timer> _rdyRetryTimers;

        private readonly Dictionary<string, Conn> _pendingConnections;
        private readonly Dictionary<string, Conn> _connections;

        private readonly List<string> _nsqdTCPAddrs = new List<string>();

        // used at connection close to force a possible reconnect
        private readonly Chan<int> _lookupdRecheckChan;
        private readonly List<string> _lookupdHTTPAddrs = new List<string>();
        private int _lookupdQueryIndex;

        private readonly WaitGroup _wg = new WaitGroup();
        private int _runningHandlers;
        private int _stopFlag;
        private int _connectedFlag;
        private readonly Once _stopHandler = new Once();
        private readonly Once _exitHandler = new Once();

        // read from this channel to block until consumer is cleanly stopped
        private readonly Chan<int> _stopChan;
        private readonly Chan<int> _exitChan;

        /// <summary>
        /// Creates a new instance of Consumer for the specified topic/channel
        /// </summary>
        /// <param name="topic">The topic.</param>
        /// <param name="channel">The channel.</param>
        public Consumer(string topic, string channel)
            : this(topic, channel, new Config())
        {
        }

        /// <summary>
        /// Creates a new instance of Consumer for the specified topic/channel
        /// </summary>
        /// <param name="topic">The topic.</param>
        /// <param name="channel">The channel.</param>
        /// <param name="config">The config. After config is passed in the values
        /// are no longer mutable (they are copied).</param>
        public Consumer(string topic, string channel, Config config)
        {
            if (string.IsNullOrEmpty(topic))
                throw new ArgumentNullException("topic");
            if (string.IsNullOrEmpty(channel))
                throw new ArgumentNullException("channel");
            if (config == null)
                throw new ArgumentNullException("config");

            config.Validate();

            if (!Protocol.IsValidTopicName(topic))
            {
                throw new ArgumentException("invalid topic name", "topic");
            }

            if (!Protocol.IsValidChannelName(channel))
            {
                throw new ArgumentException("invalid channel name", "channel");
            }

            _id = Interlocked.Increment(ref _instCount);

            _topic = topic;
            _channel = channel;
            _config = config.Clone();

            _logger = new ConsoleLogger(); // TODO: writes to stderr, not console
            _logLvl = LogLevel.Info;
            _maxInFlight = config.MaxInFlight;

            _incomingMessages = new Chan<Message>();

            _rdyRetryTimers = new Dictionary<string, Timer>();
            _pendingConnections = new Dictionary<string, Conn>();
            _connections = new Dictionary<string, Conn>();

            _lookupdRecheckChan = new Chan<int>(bufferSize: 1);

            _rng = new RNGCryptoServiceProvider();

            _stopChan = new Chan<int>();
            _exitChan = new Chan<int>();

            _wg.Add(1);

            GoFunc.Run(rdyLoop);
        }

        /// <summary>
        /// Receive on StopChan to block until <see cref="Stop()"/> completes.
        /// </summary>
        public IReceiveOnlyChan<int> StopChan
        {
            get { return _stopChan; }
        }

        /// <summary>Stats retrieves the current connection and message statistics for a Consumer</summary>
        public ConsumerStats Stats()
        {
            return new ConsumerStats
            {
                MessagesReceived = _messagesReceived,
                MessagesFinished = _messagesFinished,
                MessagesRequeued = _messagesRequeued,
                Connections = conns().Count
            };
        }

        private List<Conn> conns()
        {
            _mtx.EnterReadLock();
            try
            {
                return new List<Conn>(_connections.Values);
            }
            finally
            {
                _mtx.ExitReadLock();
            }
        }

        /// <summary>
        /// SetLogger assigns the logger to use as well as a level
        ///
        /// The logger parameter is an interface that requires the following
        /// method to be implemented (such as the the stdlib log.Logger):
        ///
        ///    Output(calldepth int, s string)
        ///
        /// </summary>
        /// <param name="l">The <see cref="ILogger"/></param>
        /// <param name="lvl">The <see cref="LogLevel"/></param>
        public void SetLogger(ILogger l, LogLevel lvl)
        {
            _logger = l;
            _logLvl = lvl;
        }

        /// <summary>
        /// SetBehaviorDelegate takes a type implementing one or more
        /// of the following interfaces that modify the behavior
        /// of the `Consumer`:
        ///
        ///    DiscoveryFilter
        ///
        /// </summary>
        /// <param name="cb">The callback</param>
        public void SetBehaviorDelegate(IDiscoveryFilter cb)
        {
            // TODO: can go-nsq take a DiscoveryFilter instead of interface{} ?
            _behaviorDelegate = cb;
        }

        /// <summary>
        /// perConnMaxInFlight calculates the per-connection max-in-flight count.
        ///
        /// This may change dynamically based on the number of connections to nsqd the Consumer
        /// is responsible for.
        /// </summary>
        private long perConnMaxInFlight()
        {
            long b = getMaxInFlight();
            int connCount = conns().Count;
            long s = (connCount == 0 ? 1 : b / connCount);
            return Math.Min(Math.Max(1, s), b);
        }

        /// <summary>
        /// IsStarved indicates whether any connections for this consumer are blocked on processing
        /// before being able to receive more messages (ie. RDY count of 0 and not exiting)
        /// </summary>
        public bool IsStarved()
        {
            foreach (var conn in conns())
            {
                // TODO: if in backoff, would IsStarved return true? what's the impact?
                // TODO: go-nsq PR, use conn.LastRDY() which does the atomic load for us
                long threshold = (long)(conn.LastRDY * 0.85);
                long inFlight = conn._messagesInFlight;
                if (inFlight >= threshold && inFlight > 0 && !conn.IsClosing)
                {
                    return true;
                }
            }
            return false;
        }

        private int getMaxInFlight()
        {
            return _maxInFlight;
        }

        /// <summary>
        /// ChangeMaxInFlight sets a new maximum number of messages this comsumer instance
        /// will allow in-flight, and updates all existing connections as appropriate.
        ///
        /// For example, ChangeMaxInFlight(0) would pause message flow
        ///
        /// If already connected, it updates the reader RDY state for each connection.
        /// </summary>
        public void ChangeMaxInFlight(int maxInFlight)
        {
            if (getMaxInFlight() == maxInFlight)
                return;

            _maxInFlight = maxInFlight;

            foreach (var c in conns())
            {
                maybeUpdateRDY(c);
            }
        }

        /// <summary>
        /// ConnectToNSQLookupd adds an nsqlookupd address to the list for this Consumer instance.
        ///
        /// If it is the first to be added, it initiates an HTTP request to discover nsqd
        /// producers for the configured topic.
        ///
        /// A goroutine is spawned to handle continual polling.
        /// </summary>
        public void ConnectToNSQLookupd(string addr)
        {
            if (string.IsNullOrEmpty(addr))
                throw new ArgumentNullException("addr");

            if (_stopFlag == 1)
                throw new Exception("consumer stopped");
            if (_runningHandlers == 0)
                throw new Exception("no handlers");

            validatedLookupAddr(addr);

            _connectedFlag = 1;

            int numLookupd;
            _mtx.EnterWriteLock();
            try
            {
                foreach (var x in _lookupdHTTPAddrs)
                {
                    if (x == addr)
                        return;
                }

                _lookupdHTTPAddrs.Add(addr);
                numLookupd = _lookupdHTTPAddrs.Count;
            }
            finally
            {
                _mtx.ExitWriteLock();
            }

            // if this is the first one, kick off the go loop
            if (numLookupd == 1)
            {
                queryLookupd();
                _wg.Add(1);
                GoFunc.Run(lookupdLoop);
            }
        }

        /// <summary>
        /// ConnectToNSQLookupd adds multiple nsqlookupd address to the list for this Consumer instance.
        ///
        /// If adding the first address it initiates an HTTP request to discover nsqd
        /// producers for the configured topic.
        ///
        /// A goroutine is spawned to handle continual polling.
        /// </summary>
        public void ConnectToNSQLookupds(IEnumerable<string> addresses)
        {
            if (addresses == null)
                throw new ArgumentNullException("addresses");

            foreach (var addr in addresses)
            {
                ConnectToNSQLookupd(addr);
            }
        }

        private void validatedLookupAddr(string addr)
        {
            if (addr.Contains("/"))
            {
                // TODO: verify this is the kind of validation we want
                new Uri(addr, UriKind.Absolute);
            }
            if (!addr.Contains(":"))
                throw new Exception("missing port");
        }

        /// <summary>
        /// poll all known lookup servers every LookupdPollInterval
        /// </summary>
        private void lookupdLoop()
        {
            // add some jitter so that multiple consumers discovering the same topic,
            // when restarted at the same time, dont all connect at once.
            var jitter = new TimeSpan((long)(_rng.Float64() * _config.LookupdPollJitter * _config.LookupdPollInterval.Ticks));

            bool doLoop = true;

            Select
                .CaseReceive(Time.After(jitter))
                .CaseReceive(_exitChan, o => doLoop = false)
                .NoDefault();

            var ticker = new Ticker(_config.LookupdPollInterval);

            using (var select =
                    Select
                        .CaseReceive(ticker.C, o => queryLookupd())
                        .CaseReceive(_lookupdRecheckChan, o => queryLookupd())
                        .CaseReceive(_exitChan, o => doLoop = false)
                        .NoDefault(defer: true))
            {
                while (doLoop)
                {
                    select.Execute();
                }
            }

            ticker.Stop();
            log(LogLevel.Info, "exiting lookupdLoop");
            _wg.Done();
        }

        /// <summary>
        /// return the next lookupd endpoint to query
        /// keeping track of which one was last used
        /// </summary>
        private string nextLookupdEndpoint()
        {
            string addr;
            int num;

            _mtx.EnterReadLock();
            try
            {
                if (_lookupdQueryIndex >= _lookupdHTTPAddrs.Count)
                {
                    _lookupdQueryIndex = 0;
                }
                addr = _lookupdHTTPAddrs[_lookupdQueryIndex];
                num = _lookupdHTTPAddrs.Count;
            }
            finally
            {
                _mtx.ExitReadLock();
            }

            _lookupdQueryIndex = (_lookupdQueryIndex + 1) % num;

            string urlString = addr;
            if (!urlString.Contains("://"))
                urlString = "http://" + addr;

            // TODO: handle parsing url better... maybe
            var u = new Uri(urlString);
            urlString = string.Format("{0}://{1}/lookup?topic={2}", u.Scheme, u.Authority, _topic);
            return urlString;
        }

        private void queryLookupd()
        {
            string endpoint = nextLookupdEndpoint();

            log(LogLevel.Info, "querying nsqlookupd {0}", endpoint);

            INsqLookupdApiResponseProducers data;
            try
            {
                data = ApiRequest.NegotiateV1("GET", endpoint);
            }
            catch (Exception ex)
            {
                log(LogLevel.Error, "error querying nsqlookupd ({0}) - {1}", endpoint, ex);
                return;
            }

            // {
            //     "channels": [],
            //     "producers": [
            //         {
            //             "broadcast_address": "jehiah-air.local",
            //             "http_port": 4151,
            //             "tcp_port": 4150
            //         }
            //     ],
            //     "timestamp": 1340152173
            // }
            var nsqAddrs = new Collection<string>();
            if (data.producers != null)
            {
                foreach (var producer in data.producers)
                {
                    var broadcastAddress = producer.broadcast_address;
                    var port = producer.tcp_port;
                    var joined = string.Format("{0}:{1}", broadcastAddress, port);
                    nsqAddrs.Add(joined);
                }
            }

            if (_behaviorDelegate != null)
            {
                nsqAddrs = _behaviorDelegate.Filter(nsqAddrs);
            }

            foreach (var addr in nsqAddrs)
            {
                try
                {
                    ConnectToNSQD(addr);
                }
                catch (Exception ex)
                {
                    if (!(ex is ErrAlreadyConnected))
                    {
                        log(LogLevel.Error, "({0}) error connecting to nsqd - {1}", addr, ex);
                    }
                }
            }
        }

        /// <summary>
        /// ConnectToNSQD takes multiple nsqd addresses to connect directly to.
        ///
        /// It is recommended to use <see cref="ConnectToNSQLookupd"/> so that topics are discovered
        /// automatically.  This method is useful when you want to connect to local instance.
        /// </summary>
        public void ConnectToNSQDs(IEnumerable<string> addresses)
        {
            if (addresses == null)
                throw new ArgumentNullException("addresses");

            foreach (var addr in addresses)
            {
                ConnectToNSQD(addr);
            }
        }

        /// <summary>
        /// ConnectToNSQD takes a nsqd address to connect directly to.
        ///
        /// It is recommended to use <see cref="ConnectToNSQLookupd"/> so that topics are discovered
        /// automatically.  This method is useful when you want to connect to a single, local,
        /// instance.
        /// </summary>
        public void ConnectToNSQD(string addr)
        {
            if (string.IsNullOrEmpty(addr))
                throw new ArgumentNullException("addr");

            if (_stopFlag == 1)
            {
                throw new Exception("consumer stopped");
            }

            if (_runningHandlers == 0)
            {
                throw new Exception("no handlers");
            }

            _connectedFlag = 1;

            var conn = new Conn(addr, _config, new ConsumerConnDelegate { r = this });
            // TODO: Check log format
            conn.SetLogger(_logger, _logLvl, string.Format("{0} [{1}/{2}] ({{0}})", _id, _topic, _channel));

            _mtx.EnterWriteLock();
            try
            {
                bool pendingOk = _pendingConnections.ContainsKey(addr);
                bool ok = _connections.ContainsKey(addr);
                if (pendingOk || ok)
                {
                    throw new ErrAlreadyConnected();
                }
                _pendingConnections[addr] = conn;
                if (!_nsqdTCPAddrs.Contains(addr))
                    _nsqdTCPAddrs.Add(addr);
            }
            finally
            {
                _mtx.ExitWriteLock();
            }

            log(LogLevel.Info, "({0}) connecting to nsqd", addr);

            var cleanupConnection = new Action(() =>
            {
                _mtx.EnterWriteLock();
                try
                {
                    _pendingConnections.Remove(addr);
                }
                finally
                {
                    _mtx.ExitWriteLock();
                }
            });

            IdentifyResponse resp;
            try
            {
                resp = conn.Connect();
            }
            catch (Exception)
            {
                cleanupConnection();
                throw;
            }

            if (resp != null)
            {
                if (resp.MaxRdyCount < getMaxInFlight())
                {
                    log(LogLevel.Warning, "({0}) max RDY count {1} < consumer max in flight {2}, truncation possible",
                        conn, resp.MaxRdyCount, getMaxInFlight());
                }
            }

            var cmd = Command.Subscribe(_topic, _channel);

            try
            {
                conn.WriteCommand(cmd);
            }
            catch (Exception ex)
            {
                cleanupConnection();
                throw new Exception(string.Format("[{0}] failed to subscribe to {1}:{2} - {3}",
                    conn, _topic, _channel, ex));
            }

            _mtx.EnterWriteLock();
            try
            {
                _pendingConnections.Remove(addr);
                _connections[addr] = conn;
            }
            finally
            {
                _mtx.ExitWriteLock();
            }

            // pre-emptive signal to existing connections to lower their RDY count
            foreach (var c in conns())
            {
                maybeUpdateRDY(c);
            }
        }

        /// <summary>
        /// DisconnectFromNSQD closes the connection to and removes the specified
        /// `nsqd` address from the list
        /// </summary>
        public void DisconnectFromNSQD(string addr)
        {
            if (string.IsNullOrEmpty(addr))
                throw new ArgumentNullException("addr");

            _mtx.EnterWriteLock();
            try
            {
                int idx = _nsqdTCPAddrs.IndexOf(addr);
                if (idx == -1)
                    throw new ErrNotConnected();

                _nsqdTCPAddrs.RemoveAt(idx);

                // TODO: PR go-nsq remove from connections/pendingConnections
                Conn pendingConn, conn;
                if (_connections.TryGetValue(addr, out conn))
                {
                    _connections.Remove(addr);
                    conn.Close();
                }
                else if (_pendingConnections.TryGetValue(addr, out pendingConn))
                {
                    _pendingConnections.Remove(addr);
                    pendingConn.Close();
                }
            }
            finally
            {
                _mtx.ExitWriteLock();
            }
        }

        /// <summary>
        /// DisconnectFromNSQLookupd removes the specified `nsqlookupd` address
        /// from the list used for periodic discovery.
        /// </summary>
        public void DisconnectFromNSQLookupd(string addr)
        {
            if (string.IsNullOrEmpty(addr))
                throw new ArgumentNullException("addr");

            _mtx.EnterWriteLock();
            try
            {
                if (!_lookupdHTTPAddrs.Contains(addr))
                    throw new ErrNotConnected();

                if (_lookupdHTTPAddrs.Count == 1)
                    throw new Exception(string.Format("cannot disconnect from only remaining nsqlookupd HTTP address {0}", addr));

                _lookupdHTTPAddrs.Remove(addr);
            }
            finally
            {
                _mtx.ExitWriteLock();
            }
        }

        internal void onConnMessage(Conn c, Message msg)
        {
            Interlocked.Decrement(ref _totalRdyCount);
            Interlocked.Increment(ref _messagesReceived);
            _incomingMessages.Send(msg);
            maybeUpdateRDY(c);
        }

        internal void onConnMessageFinished(Conn c, Message msg)
        {
            Interlocked.Increment(ref _messagesFinished);
        }

        internal void onConnMessageRequeued(Conn c, Message msg)
        {
            Interlocked.Increment(ref _messagesRequeued);
        }

        internal void onConnBackoff(Conn c)
        {
            startStopContinueBackoff(c, success: false);
        }

        internal void onConnResume(Conn c)
        {
            startStopContinueBackoff(c, success: true);
        }

        private void startStopContinueBackoff(Conn conn, bool success)
        {
            // TODO: conn isn't used
            // TODO: a REQ sets ALL connections to RDY 0. this seems to assume this service or a downstream service is having
            // TODO: intermittent issues. this may not be the case, it could be an unexpected exception we'd want to see in
            // TODO: the error log. if these are frequent it could choke message processing with backoffs. maybe this is right,
            // TODO: as it's a per Consumer (topic) backoff, wouldn't apply to Conns held by other Consumers. Action: determine
            // TODO: if this is the behavior, thought behind it, and impact when unexpected exceptions occur.

            if (inBackoffBlock())
                return;

            bool backoffUpdated;
            int backoffCounter;
            _backoffMtx.EnterWriteLock();
            try
            {
                backoffUpdated = false;
                if (success)
                {
                    if (_backoffCounter > 0)
                    {
                        _backoffCounter--;
                        backoffUpdated = true;
                    }
                }
                else
                {
                    int maxBackoffCount = (int)Math.Max(1, Math.Ceiling(
                        Math.Log(_config.MaxBackoffDuration.TotalSeconds, newBase: 2)));
                    if (_backoffCounter < maxBackoffCount)
                    {
                        _backoffCounter++;
                        backoffUpdated = true;
                    }
                }

                // TODO: PR on go-nsq to store r.backoffCounter in local variable
                backoffCounter = _backoffCounter;
            }
            finally
            {
                _backoffMtx.ExitWriteLock();
            }

            if (backoffCounter == 0 && backoffUpdated)
            {
                // exit backoff
                long count = perConnMaxInFlight();
                log(LogLevel.Warning, "exiting backoff, returning all to RDY {0}", count);
                foreach (var c in conns())
                {
                    updateRDY(c, count);
                }
            }
            else if (backoffCounter > 0)
            {
                // start or continue backoff
                var backoffDuration = backoffDurationForCount(backoffCounter);
                _backoffDuration = backoffDuration.Nanoseconds();
                Time.AfterFunc(backoffDuration, backoff);

                // TODO: review log format
                log(LogLevel.Warning, "backing off for {0:0.0000} seconds (backoff level {1}), setting all to RDY 0",
                    backoffDuration.TotalSeconds, backoffCounter);

                // send RDY 0 immediately (to *all* connections)
                foreach (var c in conns())
                {
                    updateRDY(c, 0);
                }
            }
        }

        private void backoff()
        {
            _backoffDuration = 0;

            if (_stopFlag == 1)
                return;

            // pick a random connection to test the waters
            var connections = conns();
            if (connections.Count == 0)
            {
                // backoff again
                var backoffDuration = TimeSpan.FromSeconds(1);
                _backoffDuration = backoffDuration.Nanoseconds();
                Time.AfterFunc(backoffDuration, backoff);
                return;
            }
            var idx = _rng.Intn(connections.Count);
            var choice = connections[idx];

            log(LogLevel.Warning,
                "({0}) backoff timeout expired, sending RDY 1",
                choice);
            // while in backoff only ever let 1 message at a time through
            updateRDY(choice, 1);
        }

        internal void onConnResponse(Conn c, byte[] data)
        {
            if (CLOSE_WAIT_BYTES.SequenceEqual(data))
            {
                // server is ready for us to close (it ack'd our StartClose)
                // we can assume we will not receive any more messages over this channel
                // (but we can still write back responses)
                log(LogLevel.Info, "({0}) received CLOSE_WAIT from nsqd", c);
                c.Close();
            }
        }

        internal void onConnError(Conn c, byte[] data)
        {
        }

        internal void onConnHeartbeat(Conn c)
        {
        }

        internal void onConnIOError(Conn c, Exception err)
        {
            c.Close();
        }

        internal void onConnClose(Conn c)
        {
            bool hasRDYRetryTimer = false;

            string connAddr = c.ToString();

            // remove this connections RDY count from the consumer's total
            long rdyCount = c.RDY;
            Interlocked.Add(ref _totalRdyCount, rdyCount * -1);

            _rdyRetryMtx.EnterWriteLock();
            try
            {
                Timer timer;
                if (_rdyRetryTimers.TryGetValue(connAddr, out timer))
                {
                    // stop any pending retry of an old RDY update
                    timer.Stop();
                    _rdyRetryTimers.Remove(connAddr);
                    hasRDYRetryTimer = true;
                }
            }
            finally
            {
                _rdyRetryMtx.ExitWriteLock();
            }

            int left;

            _mtx.EnterWriteLock();
            try
            {
                _connections.Remove(connAddr);
                left = _connections.Count;
            }
            finally
            {
                _mtx.ExitWriteLock();
            }

            log(LogLevel.Warning, "there are {0} connections left alive", left);

            if ((hasRDYRetryTimer || rdyCount > 0) &&
                (left == getMaxInFlight() || inBackoff()))
            {
                // we're toggling out of (normal) redistribution cases and this conn
                // had a RDY count...
                //
                // trigger RDY redistribution to make sure this RDY is moved
                // to a new connection
                _needRDYRedistributed = 1;
            }

            if (_stopFlag == 1)
            {
                if (left == 0)
                {
                    stopHandlers();
                }
                return;
            }

            int numLookupd;
            bool reconnect;

            _mtx.EnterReadLock();
            try
            {
                numLookupd = _lookupdHTTPAddrs.Count;
                reconnect = _nsqdTCPAddrs.Contains(connAddr);
            }
            finally
            {
                _mtx.ExitReadLock();
            }

            if (numLookupd > 0)
            {
                // trigger a poll of the lookupd
                Select
                    .CaseSend(_lookupdRecheckChan, 1)
                    .Default(func: null);
            }
            else if (reconnect)
            {
                // there are no lookupd and we still have this nsqd TCP address in our list...
                // try to reconnect after a bit
                GoFunc.Run(() =>
                {
                    while (true)
                    {
                        log(LogLevel.Info, "({0}) re-connecting in 15 seconds...", connAddr);
                        Thread.Sleep(TimeSpan.FromSeconds(15));
                        if (_stopFlag == 1)
                        {
                            break;
                        }
                        _mtx.EnterReadLock();
                        reconnect = _nsqdTCPAddrs.Contains(connAddr);
                        _mtx.ExitReadLock();
                        if (!reconnect)
                        {
                            log(LogLevel.Warning, "({0}) skipped reconnect after removal...", connAddr);
                            return;
                        }
                        try
                        {
                            ConnectToNSQD(connAddr);
                        }
                        catch (Exception ex)
                        {
                            if (!(ex is ErrAlreadyConnected))
                            {
                                log(LogLevel.Error, "({0}) error connecting to nsqd - {1}", connAddr, ex);
                                continue;
                            }
                            // TODO: PR go-nsq if we get DialTimeout this loop stops. check other exceptions.
                        }
                        break;
                    }
                });
            }
        }

        private TimeSpan backoffDurationForCount(int count)
        {
            var backoffDuration = new TimeSpan((long)(_config.BackoffMultiplier.Ticks * Math.Pow(2, count)));
            if (backoffDuration > _config.MaxBackoffDuration)
            {
                backoffDuration = _config.MaxBackoffDuration;
            }
            return backoffDuration;
        }

        private bool inBackoff()
        {
            return _backoffCounter > 0;
        }

        private bool inBackoffBlock()
        {
            return _backoffDuration > 0;
        }

        private void maybeUpdateRDY(Conn conn)
        {
            if (inBackoff() || inBackoffBlock())
                return;

            long remain = conn.RDY;
            long lastRdyCount = conn.LastRDY;
            long count = perConnMaxInFlight();

            // refill when at 1, or at 25%, or if connections have changed and we're imbalanced
            if (remain <= 1 || remain < (lastRdyCount / 4) || (count > 0 && count < remain))
            {
                log(LogLevel.Debug, "({0}) sending RDY {1} ({2} remain from last RDY {3})",
                    conn, count, remain, lastRdyCount);
                updateRDY(conn, count);
            }
            else
            {
                log(LogLevel.Debug, "({0}) skip sending RDY {1} ({2} remain out of last RDY {3})",
                    conn, count, remain, lastRdyCount);
            }
        }

        private void rdyLoop()
        {
            var redistributeTicker = new Ticker(TimeSpan.FromSeconds(5));

            bool doLoop = true;
            using (var select =
                    Select
                        .CaseReceive(redistributeTicker.C, o => redistributeRDY())
                        .CaseReceive(_exitChan, o => doLoop = false)
                        .NoDefault(defer: true))
            {
                while (doLoop)
                {
                    select.Execute();
                }
            }

            redistributeTicker.Stop();
            log(LogLevel.Info, "rdyLoop exiting");
            _wg.Done();
        }

        private void updateRDY(Conn c, long count)
        {
            try
            {
                if (c.IsClosing)
                    return;

                // never exceed the nsqd's configured max RDY count
                if (count > c.MaxRDY)
                    count = c.MaxRDY;

                string connAddr = c.ToString();

                // stop any pending retry of an old RDY update
                _rdyRetryMtx.EnterWriteLock();
                try
                {
                    Timer timer;
                    if (_rdyRetryTimers.TryGetValue(connAddr, out timer))
                    {
                        timer.Stop();
                        _rdyRetryTimers.Remove(connAddr);
                    }
                }
                finally
                {
                    _rdyRetryMtx.ExitWriteLock();
                }

                // never exceed our global max in flight. truncate if possible.
                // this could help a new connection get partial max-in-flight
                long rdyCount = c.RDY;
                long maxPossibleRdy = getMaxInFlight() - _totalRdyCount + rdyCount;
                if (maxPossibleRdy > 0 && maxPossibleRdy < count)
                {
                    count = maxPossibleRdy;
                }
                else if (maxPossibleRdy <= 0 && count > 0)
                {
                    // TODO: PR go-nsq: add "else" for clarity
                    if (rdyCount == 0)
                    {
                        // we wanted to exit a zero RDY count but we couldn't send it...
                        // in order to prevent eternal starvation we reschedule this attempt
                        // (if any other RDY update succeeds this timer will be stopped)
                        _rdyRetryMtx.EnterWriteLock();
                        try
                        {
                            _rdyRetryTimers[connAddr] = Time.AfterFunc(TimeSpan.FromSeconds(5),
                                () => updateRDY(c, count));
                        }
                        finally
                        {
                            _rdyRetryMtx.ExitWriteLock();
                        }
                    }
                    throw new ErrOverMaxInFlight();
                }

                sendRDY(c, count);
            }
            catch (Exception ex)
            {
                // NOTE: errors intentionally not rethrown
                log(LogLevel.Error, "({0}) error in updateRDY {1} - {2}", c, count, ex);
            }
        }

        private void sendRDY(Conn c, long count)
        {
            if (count == 0 && c.LastRDY == 0)
            {
                // no need to send. It's already that RDY count
                return;
            }

            Interlocked.Add(ref _totalRdyCount, -c.RDY + count);
            c.SetRDY(count);
            try
            {
                c.WriteCommand(Command.Ready(count));
            }
            catch (Exception ex)
            {
                log(LogLevel.Error, "({0}) error sending RDY {1} - {2}", c, count, ex);
                throw;
            }
        }

        private void redistributeRDY()
        {
            if (inBackoffBlock())
                return;

            int numConns = conns().Count;
            int maxInFlight = getMaxInFlight();
            if (numConns > maxInFlight)
            {
                log(LogLevel.Debug, "redistributing RDY state ({0} conns > {1} max_in_flight)",
                    numConns, maxInFlight);
                _needRDYRedistributed = 1;
            }

            if (inBackoff() && numConns > 1)
            {
                log(LogLevel.Debug, "redistributing RDY state (in backoff and {0} conns > 1)", numConns);
                _needRDYRedistributed = 1;
            }

            if (Interlocked.CompareExchange(ref _needRDYRedistributed, value: 0, comparand: 1) != 1)
            {
                return;
            }

            var connections = conns();
            var possibleConns = new List<Conn>();
            foreach (var c in connections)
            {
                var lastMsgDuration = DateTime.Now.Subtract(c.LastMessageTime);
                long rdyCount = c.RDY;
                log(LogLevel.Debug, "({0}) rdy: {1} (last message received {2})",
                    c, rdyCount, lastMsgDuration);
                if (rdyCount > 0 && lastMsgDuration > _config.LowRdyIdleTimeout)
                {
                    log(LogLevel.Debug, "({0}) idle connection, giving up RDY", c);
                    updateRDY(c, 0);
                }
                possibleConns.Add(c);
            }

            long availableMaxInFlight = maxInFlight - _totalRdyCount;
            if (inBackoff())
            {
                availableMaxInFlight = 1 - _totalRdyCount;
            }

            while (possibleConns.Count > 0 && availableMaxInFlight > 0)
            {
                availableMaxInFlight--;
                int i = _rng.Intn(possibleConns.Count);
                var c = possibleConns[i];
                // delete
                possibleConns.Remove(c);
                log(LogLevel.Debug, "({0}) redistributing RDY", c);
                updateRDY(c, 1);
            }
        }

        /// <summary>
        /// <see cref="Stop(bool)"/> will initiate a graceful stop of the <see cref="Consumer" /> (permanent)
        /// </summary>
        /// <param name="blockUntilStopCompletes"><c>true</c> to block until the graceful shutdown completes
        /// (default = <c>false</c>)</param>
        public void Stop(bool blockUntilStopCompletes)
        {
            Stop();

            if (blockUntilStopCompletes)
                StopChan.Receive();
        }

        /// <summary>
        /// <see cref="Stop()"/> will initiate a graceful stop of the <see cref="Consumer" /> (permanent)
        ///
        /// NOTE: receive on <see cref="StopChan"/> to block until this process completes
        /// </summary>
        public void Stop()
        {
            if (Interlocked.CompareExchange(ref _stopFlag, value: 1, comparand: 0) != 0)
            {
                return;
            }

            log(LogLevel.Info, "stopping...");

            var connections = conns();
            if (connections.Count == 0)
            {
                stopHandlers();
            }
            else
            {
                foreach (var c in connections)
                {
                    try
                    {
                        c.WriteCommand(Command.StartClose());
                    }
                    catch (Exception ex)
                    {
                        log(LogLevel.Error, "({0}) error sending CLS - {1}", c, ex);
                    }

                    // if we've waited this long handlers are blocked on processing messages
                    // so we can't just stopHandlers (if any adtl. messages were pending processing
                    // we would cause a panic on channel close)
                    //
                    // instead, we just bypass handler closing and skip to the final exit
                    Time.AfterFunc(TimeSpan.FromSeconds(30), exit);
                }
            }
        }

        private void stopHandlers()
        {
            _stopHandler.Do(() =>
            {
                log(LogLevel.Info, "stopping handlers");
                _incomingMessages.Close();
            });
        }

        /// <summary>
        /// AddHandler sets the Handler for messages received by this Consumer. This can be called
        /// multiple times to add additional handlers. Handler will have a 1:1 ratio to message handling goroutines.
        ///
        /// This panics if called after connecting to NSQD or NSQ Lookupd
        ///
        /// (see IHandler or HandlerFunc for details on implementing this interface)
        /// </summary>
        public void AddHandler(IHandler handler)
        {
            if (handler == null)
                throw new ArgumentNullException("handler");

            AddConcurrentHandlers(handler, 1);
        }

        /// <summary>
        /// AddConcurrentHandlers sets the Handler for messages received by this Consumer.  It
        /// takes a second argument which indicates the number of goroutines to spawn for
        /// message handling.
        ///
        /// This panics if called after connecting to NSQD or NSQ Lookupd
        ///
        /// (see Handler or HandlerFunc for details on implementing this interface)
        /// </summary>
        public void AddConcurrentHandlers(IHandler handler, int concurrency)
        {
            if (handler == null)
                throw new ArgumentNullException("handler");
            if (concurrency <= 0)
                throw new ArgumentOutOfRangeException("concurrency", concurrency, "concurrency must be > 0");

            if (_connectedFlag == 1)
            {
                throw new Exception("already connected");
            }

            Interlocked.Add(ref _runningHandlers, concurrency);
            for (int i = 0; i < concurrency; i++)
            {
                GoFunc.Run(() => handlerLoop(handler));
            }
        }

        private void handlerLoop(IHandler handler)
        {
            log(LogLevel.Debug, "starting Handler");

            while (true)
            {
                bool ok;
                var message = _incomingMessages.ReceiveOk(out ok);
                if (!ok)
                {
                    break;
                }

                if (shouldFailMessage(message, handler))
                {
                    message.Finish();
                    continue;
                }

                try
                {
                    handler.HandleMessage(message);
                }
                catch (Exception ex)
                {
                    log(LogLevel.Error, "Handler returned error for msg {0} - {1}", message.ID, ex);
                    if (!message.IsAutoResponseDisabled())
                        message.Requeue(null);
                    continue;
                }

                if (!message.IsAutoResponseDisabled())
                    message.Finish();
            }

            //exit:
            log(LogLevel.Debug, "stopping Handler");
            if (Interlocked.Decrement(ref _runningHandlers) == 0)
            {
                exit();
            }
        }

        private bool shouldFailMessage(Message message, IHandler handler)
        {
            if (_config.MaxAttempts > 0 && message.Attempts > _config.MaxAttempts)
            {
                log(LogLevel.Warning, "msg {0} attempted {1} times, giving up",
                    message.ID, message.Attempts);

                try
                {
                    handler.LogFailedMessage(message);
                }
                catch (Exception ex)
                {
                    log(LogLevel.Error, "LogFailedMessage returned error for msg {0} - {1}", message.ID, ex);
                }

                return true;
            }
            return false;
        }

        private void exit()
        {
            _exitHandler.Do(() =>
            {
                _exitChan.Close();
                _wg.Wait();
                _stopChan.Close();
            });
        }

        private void log(LogLevel lvl, string line, params object[] args)
        {
            // TODO: fix race condition on logger
            var logger = _logger;
            if (logger == null)
                return;

            if (_logLvl > lvl)
                return;

            // TODO: proper width formatting
            logger.Output(string.Format("{0} {1} [{2}/{3}] {4}",
                Log.Prefix(lvl), _id, _topic, _channel,
                string.Format(line, args)));
        }
    }
}
