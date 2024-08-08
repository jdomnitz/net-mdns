using System;
using Makaretu.Dns.Resolving;

namespace Makaretu.Dns
{
    /// <summary>
    ///   DNS based Service Discovery is a way of using standard DNS programming interfaces, servers,
    ///   and packet formats to browse the network for services.
    /// </summary>
    /// <seealso href="https://tools.ietf.org/html/rfc6763">RFC 6763 DNS-Based Service Discovery</seealso>
    public interface IServiceDiscovery : IDisposable
    {
        /// <summary>
        ///   Gets the multicasting service.
        /// </summary>
        /// <value>
        ///   Is used to send and receive multicast <see cref="Message">DNS messages</see>.
        /// </value>
        public IMulticastService Mdns { get; }

        /// <summary>
        ///   Add the additional records into the answers.
        /// </summary>
        /// <value>
        ///   Defaults to <b>false</b>.
        /// </value>
        /// <remarks>
        ///   Some malformed systems, such as js-ipfs and go-ipfs, only examine
        ///   the <see cref="Message.Answers"/> and not the <see cref="Message.AdditionalRecords"/>.
        ///   Setting this to <b>true</b>, will move the additional records
        ///   into the answers.
        ///   <para>
        ///   This never done for DNS-SD answers.
        ///   </para>
        /// </remarks>
        public bool AnswersContainsAdditionalRecords { get; set; }

        /// <summary>
        ///   Gets the name server.
        /// </summary>
        /// <value>
        ///   Is used to answer questions.
        /// </value>
        public NameServer NameServer { get; }

        /// <summary>
        ///   Raised when a DNS-SD response is received.
        /// </summary>
        /// <value>
        ///   Contains the service name.
        /// </value>
        /// <remarks>
        ///   <b>ServiceDiscovery</b> passively monitors the network for any answers
        ///   to a DNS-SD query. When an answer is received this event is raised.
        ///   <para>
        ///   Use <see cref="QueryAllServices"/> to initiate a DNS-SD question.
        ///   </para>
        /// </remarks>
        public event EventHandler<DomainName> ServiceDiscovered;

        /// <summary>
        ///   Raised when a service instance is discovered.
        /// </summary>
        /// <value>
        ///   Contains the service instance name.
        /// </value>
        /// <remarks>
        ///   <b>ServiceDiscovery</b> passively monitors the network for any answers.
        ///   When an answer containing a PTR to a service instance is received
        ///   this event is raised.
        /// </remarks>
        public event EventHandler<ServiceInstanceDiscoveryEventArgs> ServiceInstanceDiscovered;

        /// <summary>
        ///   Raised when a service instance is shutting down.
        /// </summary>
        /// <value>
        ///   Contains the service instance name.
        /// </value>
        /// <remarks>
        ///   <b>ServiceDiscovery</b> passively monitors the network for any answers.
        ///   When an answer containing a PTR to a service instance with a
        ///   TTL of zero is received this event is raised.
        /// </remarks>
        public event EventHandler<ServiceInstanceShutdownEventArgs> ServiceInstanceShutdown;

        /// <summary>
        ///    Asks other MDNS services to send their service names.
        /// </summary>
        /// <remarks>
        ///   When an answer is received the <see cref="ServiceDiscovered"/> event is raised.
        /// </remarks>
        public void QueryAllServices();

        /// <summary>
        ///    Asks other MDNS services to send their service names;
        ///    accepts unicast and/or broadcast answers.
        /// </summary>
        /// <remarks>
        ///   When an answer is received the <see cref="ServiceDiscovered"/> event is raised.
        /// </remarks>
        public void QueryUnicastAllServices();

        /// <summary>
        ///   Asks instances of the specified service to send details.
        /// </summary>
        /// <param name="service">
        ///   The service name to query. Typically of the form "_<i>service</i>._tcp".
        /// </param>
        /// <remarks>
        ///   When an answer is received the <see cref="ServiceInstanceDiscovered"/> event is raised.
        /// </remarks>
        /// <seealso cref="ServiceProfile.ServiceName"/>
        public void QueryServiceInstances(DomainName service);

        /// <summary>
        ///   Asks instances of the specified service with the subtype to send details.
        /// </summary>
        /// <param name="service">
        ///   The service name to query. Typically of the form "_<i>service</i>._tcp".
        /// </param>
        /// <param name="subtype">
        ///   The feature that is needed.
        /// </param>
        /// <remarks>
        ///   When an answer is received the <see cref="ServiceInstanceDiscovered"/> event is raised.
        /// </remarks>
        /// <seealso cref="ServiceProfile.ServiceName"/>
        public void QueryServiceInstances(DomainName service, string subtype);

        /// <summary>
        ///   Asks instances of the specified service to send details.
        ///   accepts unicast and/or broadcast answers.
        /// </summary>
        /// <param name="service">
        ///   The service name to query. Typically of the form "_<i>service</i>._tcp".
        /// </param>
        /// <remarks>
        ///   When an answer is received the <see cref="ServiceInstanceDiscovered"/> event is raised.
        /// </remarks>
        /// <seealso cref="ServiceProfile.ServiceName"/>
        public void QueryUnicastServiceInstances(DomainName service);

        /// <summary>
        ///   Advertise a service profile.
        /// </summary>
        /// <param name="service">
        ///   The service profile.
        /// </param>
        /// <remarks>
        ///   Any queries for the service or service instance will be answered with
        ///   information from the profile.
        ///   <para>
        ///   Besides adding the profile's resource records to the <see cref="Catalog"/> PTR records are
        ///   created to support DNS-SD and reverse address mapping (DNS address lookup).
        ///   </para>
        /// </remarks>
        public void Advertise(ServiceProfile service);

        /// <summary>
        /// Probe the network to ensure the service is unique.
        /// </summary>
        /// <param name="profile"></param>
        /// <returns>True if this service conflicts with an existing network service</returns>
        /// <exception cref="InvalidOperationException">Thrown if a shared profile is probed</exception>
        public bool Probe(ServiceProfile profile);

        /// <summary>
        ///    Sends an unsolicited MDNS response describing the
        ///    service profile.
        /// </summary>
        /// <param name="profile">
        ///   The profile to describe.
        /// </param>
        /// <param name="numberOfTimes">
        ///     How many times to announce this service profile. Range [2 - 8]
        /// </param>
        /// <remarks>
        ///   Sends a MDNS response <see cref="Message"/> containing the pointer
        ///   and resource records of the <paramref name="profile"/>.
        ///   <para>
        ///   To provide increased robustness against packet loss,
        ///   two unsolicited responses are sent one second apart.
        ///   </para>
        /// </remarks>
        public void Announce(ServiceProfile profile, int numberOfTimes = 2);

        /// <summary>
        /// Sends a goodbye message for the provided
        /// profile and removes its pointer from the name sever.
        /// </summary>
        /// <param name="profile">The profile to send a goodbye message for.</param>
        public void Unadvertise(ServiceProfile profile);

        /// <summary>
        /// Sends a goodbye message for each announced service.
        /// </summary>
        public void Unadvertise();
    }
}