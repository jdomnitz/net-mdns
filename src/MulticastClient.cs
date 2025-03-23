﻿using Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Makaretu.Dns
{
    /// <summary>
    ///   Performs the magic to send and receive datagrams over multicast
    ///   sockets.
    /// </summary>
    class MulticastClient : IDisposable
    {
        static readonly ILog log = LogManager.GetLogger(typeof(MulticastClient));

        /// <summary>
        ///   The port number assigned to Multicast DNS.
        /// </summary>
        /// <value>
        ///   Port number 5353.
        /// </value>
        public static readonly int MulticastPort = 5353;

        static readonly IPAddress MulticastAddressIp4 = IPAddress.Parse("224.0.0.251");
        static readonly IPAddress MulticastAddressIp6 = IPAddress.Parse("FF02::FB");
        static readonly IPEndPoint MdnsEndpointIp6 = new IPEndPoint(MulticastAddressIp6, MulticastPort);
        static readonly IPEndPoint MdnsEndpointIp4 = new IPEndPoint(MulticastAddressIp4, MulticastPort);

        readonly List<UdpClient> receivers;
        readonly ConcurrentDictionary<IPAddress, UdpClient> senders = new ConcurrentDictionary<IPAddress, UdpClient>();

        public event EventHandler<UdpReceiveResult> MessageReceived;

        public MulticastClient(bool useIPv4, bool useIpv6, IEnumerable<NetworkInterface> nics)
        {
            // Setup the receivers.
            receivers = new List<UdpClient>();

            UdpClient receiver4 = null;
            if (useIPv4)
            {
                receiver4 = new UdpClient(AddressFamily.InterNetwork);
                receiver4.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                receiver4.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, 255);
                receiver4.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                receiver4.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
                receivers.Add(receiver4);
            }

            UdpClient receiver6 = null;
            if (useIpv6)
            {
                receiver6 = new UdpClient(AddressFamily.InterNetworkV6);
                receiver6.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                receiver6.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IpTimeToLive, 255);
                receiver6.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
                receiver6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, MulticastPort));
                receivers.Add(receiver6);
            }

            // Get the IP addresses that we should send to.
            var addreses = nics
                .SelectMany(GetNetworkInterfaceLocalAddresses)
                .Where(a => (useIPv4 && a.AddressFamily == AddressFamily.InterNetwork)
                    || (useIpv6 && a.AddressFamily == AddressFamily.InterNetworkV6));
            foreach (var address in addreses)
            {
                if (senders.ContainsKey(address))
                {
                    continue;
                }

                var localEndpoint = new IPEndPoint(address, MulticastPort);
                var sender = new UdpClient(address.AddressFamily);
                try
                {
                    switch (address.AddressFamily)
                    {
                        case AddressFamily.InterNetwork:
                            MulticastOption mcastOption4 = new MulticastOption(MulticastAddressIp4, address);
                            receiver4.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, mcastOption4);
                            sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, 255);
                            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                            sender.Client.Bind(localEndpoint);
                            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, mcastOption4);
                            break;
                        case AddressFamily.InterNetworkV6:
                            IPv6MulticastOption mcastOption6 = new IPv6MulticastOption(MulticastAddressIp6, address.ScopeId);
                            receiver6.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, mcastOption6);
                            sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                            sender.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IpTimeToLive, 255);
                            sender.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
                            sender.Client.Bind(localEndpoint);
                            sender.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, mcastOption6);
                            break;
                        default:
                            throw new NotSupportedException($"Address family {address.AddressFamily}.");
                    }

                    receivers.Add(sender);
                    log.Debug($"Will send via {localEndpoint}");
                    if (!senders.TryAdd(address, sender)) // Should not fail
                    {
                        sender.Dispose();
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressNotAvailable)
                {
                    // VPN NetworkInterfaces
                    sender.Dispose();
                }
                catch (Exception e)
                {
                    log.Error($"Cannot setup send socket for {address}: {e.Message}", e);
                    sender.Dispose();
                }
            }

            // Start listening for messages.
            foreach (var r in receivers)
            {
                Listen(r);
            }
        }

        public async Task SendAsync(byte[] message)
        {
            foreach (var sender in senders)
            {
                try
                {
                    var endpoint = sender.Key.AddressFamily == AddressFamily.InterNetwork ? MdnsEndpointIp4 : MdnsEndpointIp6;
                    await sender.Value.SendAsync(
                        message, message.Length, 
                        endpoint)
                    .ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log.Error($"Sender {sender.Key} failure: {e.Message}");
                    // eat it.
                }
            }
        }

        void Listen(UdpClient receiver)
        {
                // ReceiveAsync does not support cancellation.  So the receiver is disposed
                // to stop it. See https://github.com/dotnet/corefx/issues/9848
                Task.Run(async () =>
                {
                    try
                    {
                        var task = receiver.ReceiveAsync();

                        _ = task.ContinueWith(x => Listen(receiver), TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.RunContinuationsAsynchronously);

                        _ = task.ContinueWith(x => MessageReceived?.Invoke(this, x.Result), TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.RunContinuationsAsynchronously);

                        await task.ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }
                });
        }

        IEnumerable<IPAddress> GetNetworkInterfaceLocalAddresses(NetworkInterface nic)
        {
            return nic
                .GetIPProperties()
                .UnicastAddresses
                .Select(x => x.Address)
                .Where(x => x.AddressFamily != AddressFamily.InterNetworkV6 || x.IsIPv6LinkLocal)
                ;
        }

        #region IDisposable Support

        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    MessageReceived = null;

                    foreach (var receiver in receivers)
                    {
                        try
                        {
                            receiver.Dispose();
                        }
                        catch
                        {
                            // eat it.
                        }
                    }
                    receivers.Clear();
                    senders.Clear();
                }

                disposedValue = true;
            }
        }

        ~MulticastClient()
        {
            Dispose(false);
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
