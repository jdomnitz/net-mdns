using Common.Logging;
using Common.Logging.Simple;
using Makaretu.Dns;
using System;
using System.Linq;

namespace Spike
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Multicast DNS spike");

            // set logger factory
            var properties = new Common.Logging.Configuration.NameValueCollection
            {
                ["level"] = "TRACE",
                ["showLogName"] = "true",
                ["showDateTime"] = "true",
                ["dateTimeFormat"] = "HH:mm:ss.fff"

            };
            LogManager.Adapter = new ConsoleOutLoggerFactoryAdapter(properties);

            var mdns = new MulticastService();

            foreach (var a in MulticastService.GetIPAddresses())
            {
                Console.WriteLine($"IP address {a}");
            }

            mdns.QueryReceived += (s, e) =>
            {
                var names = e.Message.Questions
                    .Select(q => q.Name + " " + q.Type);
                Console.WriteLine($"got a query for {String.Join(", ", names)}");
            };
            mdns.AnswerReceived += (s, e) =>
            {
                var names = e.Message.Answers
                    .Select(q => q.Name + " " + q.Type)
                    .Distinct();
                Console.WriteLine($"got answer for {String.Join(", ", names)}");
            };
            mdns.NetworkInterfaceDiscovered += (s, e) =>
            {
                foreach (var nic in e.NetworkInterfaces)
                {
                    Console.WriteLine($"discovered NIC '{nic.Name}'");
                }
            };

            var ipfs1 = new ServiceProfile("ipfs1", "_ipfs-discovery._udp", 5010);
            var z1 = new ServiceProfile("z1", "_zservice._udp", 5012);
            var x1 = new ServiceProfile("x1", "_xservice._tcp", 5011);

            var sd = new ServiceDiscovery(mdns);
            mdns.Start();

            if (!sd.Probe(ipfs1))
            {
                sd.Advertise(ipfs1);
                sd.Announce(ipfs1);
            }

            if (!sd.Probe(x1))
            {
                sd.Advertise(x1);
                sd.Announce(x1);
            }

            z1.AddProperty("foo", "bar");
            if (!sd.Probe(z1))
            {
                sd.Advertise(z1);
                sd.Announce(z1);
            }

            Console.ReadKey();
        }
    }
}
