using Common.Logging;
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
    internal class MulticastClient : IDisposable
    {
        private static readonly ILog Logger = LogManager.GetLogger<MulticastClient>();

        /// <summary>
        ///   The port number assigned to Multicast DNS.
        /// </summary>
        /// <value>
        ///   Port number 5353.
        /// </value>
        public static readonly int MulticastPort = 5353;

        private static readonly IPAddress MulticastAddressIPv4 = IPAddress.Parse("224.0.0.251");

        private readonly List<UdpClient> _receivers = [];
        private readonly ConcurrentDictionary<IPAddress, UdpClient> _senders = new();
        private readonly Func<IPAddress, IPv6MulticastAddressScope> _ipv6MulticastScopeSelector;

        private bool _isDisposed = false;

        public event EventHandler<UdpReceiveResult> MessageReceived;

        public MulticastClient(bool useIPv4, bool useIpv6, IEnumerable<NetworkInterface> nics, Func<IPAddress, IPv6MulticastAddressScope> ipv6MulticastScopeSelector)
        {
            _ipv6MulticastScopeSelector = ipv6MulticastScopeSelector;

            // Setup the receivers.
            UdpClient receiver4 = null;
            if (useIPv4)
            {
                receiver4 = new UdpClient(AddressFamily.InterNetwork);
                receiver4.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                receiver4.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, 255);
                receiver4.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                receiver4.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
                _receivers.Add(receiver4);
            }

            UdpClient receiver6 = null;
            if (useIpv6)
            {
                receiver6 = new UdpClient(AddressFamily.InterNetworkV6);
                receiver6.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                receiver6.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IpTimeToLive, 255);
                receiver6.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
                receiver6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, MulticastPort));
                _receivers.Add(receiver6);
            }

            // Get the IP addresses that we should send to.
            var addresses = nics
                .SelectMany(GetNetworkInterfaceLocalAddresses)
                .Where(a => (useIPv4 && a.AddressFamily == AddressFamily.InterNetwork)
                    || (useIpv6 && a.AddressFamily == AddressFamily.InterNetworkV6));
            foreach (var address in addresses)
            {
                if (_senders.ContainsKey(address))
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
                            var mcastOption4 = new MulticastOption(MulticastAddressIPv4, address);
                            receiver4?.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, mcastOption4);
                            sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, 255);
                            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                            sender.Client.Bind(localEndpoint);
                            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, mcastOption4);
                            break;

                        case AddressFamily.InterNetworkV6:
                            var mcastOption6 = new IPv6MulticastOption(GetMulticastAddressIPv6(address), address.ScopeId);
                            receiver6?.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, mcastOption6);
                            sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                            sender.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IpTimeToLive, 255);
                            sender.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
                            sender.Client.Bind(localEndpoint);
                            sender.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, mcastOption6);
                            break;

                        default:
                            throw new NotSupportedException($"Address family {address.AddressFamily}.");
                    }

                    _receivers.Add(sender);
                    Logger.Debug($"Will send via {localEndpoint}");
                    if (!_senders.TryAdd(address, sender)) // Should not fail
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
                    Logger.Error($"Cannot setup send socket for {address}: {e.Message}", e);
                    sender.Dispose();
                }
            }

            // Start listening for messages.
            foreach (var r in _receivers)
            {
                Listen(r);
            }
        }

        public async Task SendAsync(byte[] message)
        {
            foreach (var sender in _senders)
            {
                try
                {
                    var multicastAddress = sender.Key.AddressFamily == AddressFamily.InterNetwork
                        ? MulticastAddressIPv4
                        : GetMulticastAddressIPv6(sender.Key);

                    await sender.Value.SendAsync(message, message.Length, new(multicastAddress, MulticastPort)).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Logger.Error($"Sender {sender.Key} failure: {e.Message}");
                    // eat it.
                }
            }
        }

        private void Listen(UdpClient receiver)
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
                    // Ignore
                }
            });
        }

        private IEnumerable<IPAddress> GetNetworkInterfaceLocalAddresses(NetworkInterface nic)
        {
            return nic
                .GetIPProperties()
                .UnicastAddresses
                .Select(x => x.Address)
                .Where(x => x.AddressFamily != AddressFamily.InterNetworkV6 || x.IsIPv6LinkLocal);
        }

        private IPAddress GetMulticastAddressIPv6(IPAddress localAddress)
        {
            return IPAddress.Parse($"FF0{(byte)_ipv6MulticastScopeSelector(localAddress):X1}::FB");
        }

        #region IDisposable Support

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    MessageReceived = null;

                    foreach (var receiver in _receivers)
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
                    _receivers.Clear();
                    _senders.Clear();
                }

                _isDisposed = true;
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

        #endregion IDisposable Support
    }
}
