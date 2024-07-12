using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;

namespace Makaretu.Dns
{
    /// <summary>
    ///   Muticast Domain Name Service.
    /// </summary>
    /// <remarks>
    ///   Sends and receives DNS queries and answers via the multicast mechanism
    ///   defined in <see href="https://tools.ietf.org/html/rfc6762"/>.
    ///   <para>
    ///   Use <see cref="Start"/> to start listening for multicast messages.
    ///   One of the events, <see cref="QueryReceived"/> or <see cref="AnswerReceived"/>, is
    ///   raised when a <see cref="Message"/> is received.
    ///   </para>
    /// </remarks>
    public class MulticastService : IMulticastService
    {
        // IP header (20 bytes for IPv4; 40 bytes for IPv6) and the UDP header(8 bytes).
        private const int packetOverhead = 48;
        private const int maxDatagramSize = Message.MaxLength;

        private static readonly TimeSpan maxLegacyUnicastTTL = TimeSpan.FromSeconds(10);
        private static readonly ILog log = LogManager.GetLogger(typeof(MulticastService));

        private List<NetworkInterface> knownNics = new List<NetworkInterface>();
        private int maxPacketSize;

        /// <summary>
        /// When this bit is set in a question, it indicates that the querier is willing to accept unicast replies in response to this specific query,
        /// as well as the usual multicast responses.
        /// </summary>
        public const int UNICAST_RESPONSE_BIT = 0x8000;
        /// <summary>
        /// If the record is one that has been verified unique, the host sets the most significant bit of the rrclass field of the resource record.
        /// This bit, the cache-flush bit, tells neighboring hosts that this is not a shared record type.
        /// </summary>
        public const int CACHE_FLUSH_BIT = 0x8000;
        /// <summary>
        ///   Recently sent messages.
        /// </summary>
        private readonly RecentMessages sentMessages = new RecentMessages();

        /// <summary>
        ///   Recently received messages.
        /// </summary>
        private readonly RecentMessages receivedMessages = new RecentMessages();

        /// <summary>
        ///   The multicast client.
        /// </summary>
        private MulticastClient client;

        /// <summary>
        ///   Use to send unicast IPv4 answers.
        /// </summary>
        private readonly UdpClient unicastClientIp4;

        /// <summary>
        ///   Use to send unicast IPv6 answers.
        /// </summary>
        private readonly UdpClient unicastClientIp6;

        /// <summary>
        ///   Function used for listening filtered network interfaces.
        /// </summary>
        private readonly Func<IEnumerable<NetworkInterface>, IEnumerable<NetworkInterface>> networkInterfacesFilter;

        /// <summary>
        ///   Raised when any local MDNS service sends a query.
        /// </summary>
        /// <value>
        ///   Contains the query <see cref="Message"/>.
        /// </value>
        /// <remarks>
        ///   Any exception throw by the event handler is simply logged and
        ///   then forgotten.
        /// </remarks>
        /// <seealso cref="SendQuery(Message)"/>
        public event EventHandler<MessageEventArgs> QueryReceived;

        /// <summary>
        ///   Raised when any link-local MDNS service responds to a query.
        /// </summary>
        /// <value>
        ///   Contains the answer <see cref="Message"/>.
        /// </value>
        /// <remarks>
        ///   Any exception throw by the event handler is simply logged and
        ///   then forgotten.
        /// </remarks>
        public event EventHandler<MessageEventArgs> AnswerReceived;

        /// <summary>
        ///   Raised when a DNS message is received that cannot be decoded.
        /// </summary>
        /// <value>
        ///   The DNS message as a byte array.
        /// </value>
        public event EventHandler<byte[]> MalformedMessage;

        /// <summary>
        ///   Raised when one or more network interfaces are discovered.
        /// </summary>
        /// <value>
        ///   Contains the network interface(s).
        /// </value>
        public event EventHandler<NetworkInterfaceEventArgs> NetworkInterfaceDiscovered;

        /// <summary>
        ///   Create a new instance of the <see cref="MulticastService"/> class.
        /// </summary>
        /// <param name="filter">
        ///   Multicast listener will be bound to result of filtering function.
        /// </param>
        public MulticastService(Func<IEnumerable<NetworkInterface>, IEnumerable<NetworkInterface>> filter = null)
        {
            networkInterfacesFilter = filter;

            UseIpv4 = Socket.OSSupportsIPv4;
            if (UseIpv4)
                unicastClientIp4 = new UdpClient(AddressFamily.InterNetwork);
            UseIpv6 = Socket.OSSupportsIPv6;
            if (UseIpv6)
                unicastClientIp6 = new UdpClient(AddressFamily.InterNetworkV6);
            IgnoreDuplicateMessages = true;
        }

        /// <summary>
        ///   Send and receive on IPv4.
        /// </summary>
        /// <value>
        ///   Defaults to <b>true</b> if the OS supports it.
        /// </value>
        public bool UseIpv4 { get; set; }

        /// <summary>
        ///   Send and receive on IPv6.
        /// </summary>
        /// <value>
        ///   Defaults to <b>true</b> if the OS supports it.
        /// </value>
        public bool UseIpv6 { get; set; }

        /// <summary>
        ///   Determines if received messages are checked for duplicates.
        /// </summary>
        /// <value>
        ///   <b>true</b> to ignore duplicate messages. Defaults to <b>true</b>.
        /// </value>
        /// <remarks>
        ///   When set, a message that has been received within the last second
        ///   will be ignored.
        /// </remarks>
        public bool IgnoreDuplicateMessages { get; set; }

        /// <summary>
        /// Determines whether loopback interfaces should be excluded when other network interfaces are available
        /// </summary>
        /// <value>
        /// <b>true</b> to always include loopback interfaces.
        /// <b>false</b> to only include loopback interfaces when no other interfaces exist.
        /// Defaults to <b>false</b>.
        /// </value>
        public static bool IncludeLoopbackInterfaces { get; set; } = false;

        /// <summary>
        /// Allow answering queries in unicast. When multiple services are sharing a port this should be set to false, otherwise true.
        /// </summary>
        /// <b>true</b> to respond to unicast queries with unicast responses.
        /// <b>false</b> to always answer queries with unicast.
        /// Defaults to <b>true</b>.
        public static bool EnableUnicastAnswers { get; set; } = true;

        /// <summary>
        /// Per https://tools.ietf.org/html/rfc6762 section 10: All records containing
        /// Host in the record OR Rdata should have a default TTL of 2 mins
        /// </summary>
        public static TimeSpan HostRecordTTL = TimeSpan.FromSeconds(120);
        /// <summary>
        /// Per https://tools.ietf.org/html/rfc6762 section 10:
        /// All records NOT containing Host in the record OR Rdata should have a default TTL of 75 mins
        /// </summary>
        public static TimeSpan NonHostTTL = TimeSpan.FromMinutes(75);

        /// <summary>
        ///   Get the network interfaces that are useable.
        /// </summary>
        /// <returns>
        ///   A sequence of <see cref="NetworkInterface"/>.
        /// </returns>
        /// <remarks>
        ///   The following filters are applied
        ///   <list type="bullet">
        ///   <item><description>interface is enabled</description></item>
        ///   <item><description>interface is not a loopback</description></item>
        ///   </list>
        ///   <para>
        ///   If no network interface is operational, then the loopback interface(s)
        ///   are included (127.0.0.1 and/or ::1).
        ///   </para>
        /// </remarks>
        public static IEnumerable<NetworkInterface> GetNetworkInterfaces()
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && !nic.IsReceiveOnly && nic.SupportsMulticast)
                .Where(nic => IncludeLoopbackInterfaces || (nic.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                .ToArray();
            if (nics.Length > 0)
                return nics;

            // Special case: no operational NIC, then use loopbacks.
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && !nic.IsReceiveOnly && nic.SupportsMulticast);
        }

        /// <summary>
        ///   Get the IP addresses of the local machine.
        /// </summary>
        /// <returns>
        ///   A sequence of IP addresses of the local machine.
        /// </returns>
        /// <remarks>
        ///   The loopback addresses (127.0.0.1 and ::1) are NOT included in the
        ///   returned sequences.
        /// </remarks>
        public static IEnumerable<IPAddress> GetIPAddresses()
        {
            return GetNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address);
        }

        /// <summary>
        ///   Get the link local IP addresses of the local machine.
        /// </summary>
        /// <returns>
        ///   A sequence of IP addresses.
        /// </returns>
        /// <remarks>
        ///   All IPv4 addresses are considered link local.
        /// </remarks>
        /// <seealso href="https://en.wikipedia.org/wiki/Link-local_address"/>
        public static IEnumerable<IPAddress> GetLinkLocalAddresses()
        {
            return GetIPAddresses()
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork ||
                    (a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6LinkLocal));
        }

        /// <summary>
        ///   Start the service.
        /// </summary>
        public void Start()
        {
            maxPacketSize = maxDatagramSize - packetOverhead;

            knownNics.Clear();

            FindNetworkInterfaces();
        }

        /// <summary>
        ///   Stop the service.
        /// </summary>
        /// <remarks>
        ///   Clears all the event handlers.
        /// </remarks>
        public void Stop()
        {
            // All event handlers are cleared.
            QueryReceived = null;
            AnswerReceived = null;
            NetworkInterfaceDiscovered = null;
#if NETSTANDARD1_1_OR_GREATER || NETCOREAPP1_0_OR_GREATER || NET471_OR_GREATER
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            }
#else
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            }
#endif
            // Stop current UDP listener
            client?.Dispose();
            client = null;
        }

        private void OnNetworkAddressChanged(object sender, EventArgs e) => FindNetworkInterfaces();

        private void FindNetworkInterfaces()
        {
            log.Debug("Finding network interfaces");

            try
            {
                var currentNics = GetNetworkInterfaces().ToList();

                var newNics = new List<NetworkInterface>();
                var oldNics = new List<NetworkInterface>();

                foreach (var nic in knownNics.Where(k => !currentNics.Any(n => k.Id == n.Id)))
                {
                    oldNics.Add(nic);

                    if (log.IsDebugEnabled)
                    {
                        log.Debug($"Removed nic '{nic.Name}'.");
                    }
                }

                foreach (var nic in currentNics.Where(nic => !knownNics.Any(k => k.Id == nic.Id)))
                {
                    newNics.Add(nic);

                    if (log.IsDebugEnabled)
                    {
                        log.Debug($"Found nic '{nic.Name}'.");
                    }
                }

                knownNics = currentNics;

                // Only create client if something has change.
                if (newNics.Count > 0 || oldNics.Count > 0)
                {
                    client?.Dispose();
                    client = new MulticastClient(UseIpv4, UseIpv6, networkInterfacesFilter?.Invoke(knownNics) ?? knownNics);
                    client.MessageReceived += OnDnsMessage;
                }

                // Tell others.
                if (newNics.Count > 0)
                {
                    NetworkInterfaceDiscovered?.Invoke(this, new NetworkInterfaceEventArgs
                    {
                        NetworkInterfaces = newNics
                    });
                }

                // Magic from @eshvatskyi
                //
                // I've seen situation when NetworkAddressChanged is not triggered
                // (wifi off, but NIC is not disabled, wifi - on, NIC was not changed
                // so no event). Rebinding fixes this.
                //
                // Do magic only on Windows.
#if NETSTANDARD1_1_OR_GREATER || NETCOREAPP1_0_OR_GREATER || NET471_OR_GREATER
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                    NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
                }
#else
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                    NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
                }
#endif
            }
            catch (Exception e)
            {
                log.Error("FindNics failed", e);
            }
        }

        /// <inheritdoc />
        public Task<Message> ResolveAsync(Message request, CancellationToken cancel = default)
        {
            var tsc = new TaskCompletionSource<Message>();

            void checkResponse(object s, MessageEventArgs e)
            {
                var response = e.Message;
                if (request.Questions.All(q => response.Answers.Any(a => a.Name == q.Name)))
                {
                    AnswerReceived -= checkResponse;
                    tsc.SetResult(response);
                }
            }

            cancel.Register(() =>
            {
                AnswerReceived -= checkResponse;
                tsc.TrySetCanceled();
            });

            AnswerReceived += checkResponse;
            SendQuery(request);

            return tsc.Task;
        }

        /// <summary>
        ///   Ask for answers about a name.
        /// </summary>
        /// <param name="name">
        ///   A domain name that should end with ".local", e.g. "myservice.local".
        /// </param>
        /// <param name="class">
        ///   The class, defaults to <see cref="DnsClass.IN"/>.
        /// </param>
        /// <param name="type">
        ///   The question type, defaults to <see cref="DnsType.ANY"/>.
        /// </param>
        /// <remarks>
        ///   Answers to any query are obtained on the <see cref="AnswerReceived"/>
        ///   event.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        ///   When the service has not started.
        /// </exception>
        public void SendQuery(DomainName name, DnsClass @class = DnsClass.IN, DnsType type = DnsType.ANY)
        {
            var msg = new Message
            {
                Opcode = MessageOperation.Query,
                QR = false
            };
            msg.Questions.Add(new Question
            {
                Name = name,
                Class = @class,
                Type = type
            });

            SendQuery(msg);
        }

        /// <summary>
        ///   Ask for answers about a name and accept unicast and/or broadcast response.
        /// </summary>
        /// <param name="name">
        ///   A domain name that should end with ".local", e.g. "myservice.local".
        /// </param>
        /// <param name="class">
        ///   The class, defaults to <see cref="DnsClass.IN"/>.
        /// </param>
        /// <param name="type">
        ///   The question type, defaults to <see cref="DnsType.ANY"/>.
        /// </param>
        /// <remarks>
        ///   Send a "QU" question (unicast). The most significant bit of the Class is set.
        ///   Answers to any query are obtained on the <see cref="AnswerReceived"/>
        ///   event.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        ///   When the service has not started.
        /// </exception>
        public void SendUnicastQuery(DomainName name, DnsClass @class = DnsClass.IN, DnsType type = DnsType.ANY)
        {
            var msg = new Message
            {
                Opcode = MessageOperation.Query,
                QR = false
            };
            msg.Questions.Add(new Question
            {
                Name = name,
                Class = (DnsClass)((ushort)@class | UNICAST_RESPONSE_BIT),
                Type = type
            });

            SendQuery(msg);
        }

        /// <summary>
        ///   Ask for answers.
        /// </summary>
        /// <param name="msg">
        ///   A query message.
        /// </param>
        /// <remarks>
        ///   Answers to any query are obtained on the <see cref="AnswerReceived"/>
        ///   event.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        ///   When the service has not started.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        ///   When the serialized <paramref name="msg"/> is too large.
        /// </exception>
        public void SendQuery(Message msg)
        {
            UpdateTTL(msg, false);
            Send(msg, false);
        }

        /// <summary>
        ///   Send an answer to a query.
        /// </summary>
        /// <param name="answer">
        ///   The answer message.
        /// </param>
        /// <param name="checkDuplicate">
        ///   If <b>true</b>, then if the same <paramref name="answer"/> was
        ///   recently sent it will not be sent again.
        /// </param>
        /// <param name="unicastEndpoint">
        ///     If defined, will generate a unicast response to the provided endpoint
        /// </param>
        /// <exception cref="InvalidOperationException">
        ///   When the service has not started.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        ///   When the serialized <paramref name="answer"/> is too large.
        /// </exception>
        /// <remarks>
        ///   <para>
        ///   The <see cref="Message.AA"/> flag is set to true,
        ///   the <see cref="Message.Id"/> set to zero and any questions are removed.
        ///   </para>
        ///   <para>
        ///   The <paramref name="answer"/> is <see cref="Message.Truncate">truncated</see>
        ///   if exceeds the maximum packet length.
        ///   </para>
        ///   <para>
        ///   <paramref name="checkDuplicate"/> should always be <b>true</b> except
        ///   when <see href="https://tools.ietf.org/html/rfc6762#section-8.1">answering a probe</see>.
        ///   </para>
        ///   <note type="caution">
        ///   If possible the <see cref="SendAnswer(Message, MessageEventArgs, bool, IPEndPoint)"/>
        ///   method should be used, so that legacy unicast queries are supported.
        ///   </note>
        /// </remarks>
        /// <see cref="QueryReceived"/>
        /// <seealso cref="Message.CreateResponse"/>
        public void SendAnswer(Message answer, bool checkDuplicate = true, IPEndPoint unicastEndpoint = null)
        {
            // All MDNS answers are authoritative and have a transaction
            // ID of zero.
            answer.AA = true;
            answer.Id = 0;
            answer.Opcode = MessageOperation.Query;
            answer.RA = false;
            answer.AD = false;
            answer.CD = false;

            // All MDNS answers must not contain any questions.
            answer.Questions.Clear();

            answer.Truncate(maxPacketSize);

            UpdateTTL(answer, false);
            Send(answer, checkDuplicate, unicastEndpoint);
        }

        /// <summary>
        ///   Send an answer to a query.
        /// </summary>
        /// <param name="answer">
        ///   The answer message.
        /// </param>
        /// <param name="query">
        ///   The query that is being answered.
        /// </param>
        /// <param name="checkDuplicate">
        ///   If <b>true</b>, then if the same <paramref name="answer"/> was
        ///   recently sent it will not be sent again.
        /// </param>
        /// <param name="endPoint">
        ///     The endpoint to send data (unicast) or null (multicast)
        /// </param>
        /// <exception cref="InvalidOperationException">
        ///   When the service has not started.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        ///   When the serialized <paramref name="answer"/> is too large.
        /// </exception>
        /// <remarks>
        ///   <para>
        ///   If the <paramref name="query"/> is a standard multicast query (sent to port 5353), then
        ///   <see cref="SendAnswer(Message, bool, IPEndPoint)"/> is called.
        ///   </para>
        ///   <para>
        ///   Otherwise a legacy unicast response is sent to sender's end point.
        ///   The <see cref="Message.AA"/> flag is set to true,
        ///   the <see cref="Message.Id"/> is set to query's ID,
        ///   the <see cref="Message.Questions"/> is set to the query's questions,
        ///   and all resource record TTLs have a max value of 10 seconds.
        ///   </para>
        ///   <para>
        ///   The <paramref name="answer"/> is <see cref="Message.Truncate">truncated</see>
        ///   if exceeds the maximum packet length.
        ///   </para>
        ///   <para>
        ///   <paramref name="checkDuplicate"/> should always be <b>true</b> except
        ///   when <see href="https://tools.ietf.org/html/rfc6762#section-8.1">answering a probe</see>.
        ///   </para>
        /// </remarks>
        public void SendAnswer(Message answer, MessageEventArgs query, bool checkDuplicate = true, IPEndPoint endPoint = null)
        {
            if (!query.IsLegacyUnicast)
            {
                SendAnswer(answer, checkDuplicate, endPoint);
                return;
            }

            answer.AA = true;
            answer.Id = query.Message.Id;
            answer.Questions.Clear();
            answer.Questions.AddRange(query.Message.Questions);
            answer.Truncate(maxPacketSize);

            UpdateTTL(answer, true);

            Send(answer, checkDuplicate, query.RemoteEndPoint);
        }

        internal void Send(Message msg, bool checkDuplicate, IPEndPoint remoteEndPoint = null)
        {
            var packet = msg.ToByteArray();
            if (packet.Length > maxPacketSize)
            {
                throw new ArgumentOutOfRangeException($"Exceeds max packet size of {maxPacketSize}.");
            }

            if (checkDuplicate && !sentMessages.TryAdd(packet))
            {
                return;
            }

            if (remoteEndPoint == null)
            {
                // Standard multicast reponse
                client?.SendAsync(packet).GetAwaiter().GetResult();
            }
            // Unicast response
            else
            {
                var unicastClient = (remoteEndPoint.Address.AddressFamily == AddressFamily.InterNetwork)
                    ? unicastClientIp4 : unicastClientIp6;
                unicastClient?.SendAsync(packet, packet.Length, remoteEndPoint).GetAwaiter().GetResult();
            }
        }

        private static void UpdateTTL(Message msg, bool legacy)
        {
            foreach (var r in msg.Answers)
                updateRecord(r, legacy);

            foreach (var r in msg.AdditionalRecords)
                updateRecord(r, legacy);

            foreach (var r in msg.AuthorityRecords)
                updateRecord(r, legacy);
        }

        private static void updateRecord(ResourceRecord record, bool legacy)
        {
            switch (record.Type)
            {
                case DnsType.A:
                case DnsType.AAAA:
                case DnsType.SRV:
                case DnsType.HINFO:
                case DnsType.PTR:
                    if (record.TTL != TimeSpan.Zero)
                        record.TTL = HostRecordTTL;
                    break;
                default:
                    if (record.TTL != TimeSpan.Zero)
                        record.TTL = NonHostTTL;
                    break;
            }
            if (legacy && record.TTL > maxLegacyUnicastTTL)
                record.TTL = maxLegacyUnicastTTL;
        }

        /// <summary>
        ///   Called by the MulticastClient when a DNS message is received.
        /// </summary>
        /// <param name="sender">
        ///   The <see cref="MulticastClient"/> that got the message.
        /// </param>
        /// <param name="result">
        ///   The received message <see cref="UdpReceiveResult"/>.
        /// </param>
        /// <remarks>
        ///   Decodes the <paramref name="result"/> and then raises
        ///   either the <see cref="QueryReceived"/> or <see cref="AnswerReceived"/> event.
        ///   <para>
        ///   Multicast DNS messages received with an OPCODE or RCODE other than zero
        ///   are silently ignored.
        ///   </para>
        ///   <para>
        ///   If the message cannot be decoded, then the <see cref="MalformedMessage"/>
        ///   event is raised.
        ///   </para>
        /// </remarks>
        public void OnDnsMessage(object sender, UdpReceiveResult result)
        {
            // If recently received, then ignore.
            if (IgnoreDuplicateMessages && !receivedMessages.TryAdd(result.Buffer))
            {
                return;
            }

            var msg = new Message();
            try
            {
                msg.Read(result.Buffer, 0, result.Buffer.Length);
            }
            catch (Exception e)
            {
                log.Warn("Received malformed message", e);
                MalformedMessage?.Invoke(this, result.Buffer);
                return; // eat the exception
            }

            //Section 18.3 An opcode other than 0 must be silently ignored
            if (msg.Opcode != MessageOperation.Query || msg.Status != MessageStatus.NoError)
            {
                return;
            }

            // Dispatch the message.
            try
            {
                if (msg.IsQuery && msg.Questions.Count > 0)
                {
                    QueryReceived?.Invoke(this, new MessageEventArgs { Message = msg, RemoteEndPoint = result.RemoteEndPoint });
                }
                else if (msg.IsResponse && msg.Answers.Count > 0)
                {
                    AnswerReceived?.Invoke(this, new MessageEventArgs { Message = msg, RemoteEndPoint = result.RemoteEndPoint });
                }
            }
            catch (Exception e)
            {
                log.Error("Receive handler failed", e);
                // eat the exception
            }
        }

        #region IDisposable Support

        /// <inheritdoc />
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                //Dispose of the clients
                unicastClientIp4?.Dispose();
                unicastClientIp6?.Dispose();
                Stop();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion IDisposable Support
    }
}