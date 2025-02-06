using System.Buffers.Binary;
using System.Net;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace OrionEventServerTester
{
    internal class Program
    {
        public class Tester : WebSocketBehavior
        {
            protected override void OnMessage(MessageEventArgs e)
            {
                Console.WriteLine($"Received message. Size: {BinaryPrimitives.ReadUInt16LittleEndian(e.RawData)}");
            }
        }

        static void Main(string[] args)
        {
            var wssv = new WebSocketServer(IPAddress.Loopback, 54321, false);

            wssv.AddWebSocketService<Tester>("/");
            wssv.Start();

            Console.WriteLine("WS server started");

            Console.ReadKey(true);
            wssv.Stop();
        }
    }
}
