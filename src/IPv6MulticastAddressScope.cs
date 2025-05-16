namespace Makaretu.Dns
{
    /// <summary>
    /// The IPv6 multicast address scopes.
    /// </summary>
    public enum IPv6MulticastAddressScope
    {
        /// <summary>
        /// The Interface-Local IPv6 multicast address scope.
        /// </summary>
        InterfaceLocal = 1,

        /// <summary>
        /// The Link-Local IPv6 multicast address scope.
        /// </summary>
        LinkLocal = 2,

        /// <summary>
        /// The Realm-Local IPv6 multicast address scope.
        /// </summary>
        RealmLocal = 3,

        /// <summary>
        /// The Admin-Local IPv6 multicast address scope.
        /// </summary>
        AdminLocal = 4,

        /// <summary>
        /// The Site-Local IPv6 multicast address scope.
        /// </summary>
        SiteLocal = 5,

        /// <summary>
        /// The Organization-Local IPv6 multicast address scope.
        /// </summary>
        OrganizationLocal = 8,

        /// <summary>
        /// The Global IPv6 multicast address scope.
        /// </summary>
        Global = 14
    }
}
