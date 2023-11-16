using Makaretu.Dns;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

var mdnsService = new ServiceProfile("10.0.1.50", "_videohub._tcp", 9990);
var mdnsService2 = new ServiceProfile("10.0.1.50", "_blackmagic._tcp", 9990);
var sd = new ServiceDiscovery();
sd.Advertise(mdnsService);
sd.Advertise(mdnsService2);
while (true) {
	Console.WriteLine("Advertise");
	sd.Announce(mdnsService);
	sd.Announce(mdnsService2);
	Thread.Sleep(5000);
}