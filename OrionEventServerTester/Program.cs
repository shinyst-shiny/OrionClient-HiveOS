using OrionEventLib;
using OrionEventLib.Events;
using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace OrionEventServerTester
{
    internal class Program
    {
        public class Tester : WebSocketBehavior
        {
            Dictionary<(EventTypes, SubEventTypes), Type> _eventTypes = new Dictionary<(EventTypes, SubEventTypes), Type>();

            public Tester()
            {
                var eventType = GetEnumerableOfType<OrionEvent>(null).ToList();

                foreach(var evType in eventType)
                {
                    _eventTypes.TryAdd((evType.EventType, evType.SubEventType), evType.GetType());
                }
            }

            protected override void OnMessage(MessageEventArgs e)
            {
                //Going to assume that the data isn't being received in chunks
                EventDeserializer reader = new EventDeserializer(e.RawData);
                reader.Skip(2); //Skip size

                if(!_eventTypes.TryGetValue(((EventTypes)reader.ReadByte(), (SubEventTypes)reader.ReadByte()), out var t))
                {
                    Console.WriteLine("Error: Invalid message");
                    return;
                }

                var orionEvent = (OrionEvent)Activator.CreateInstance(t);
                orionEvent.Deserialize(reader);

                Console.WriteLine(orionEvent);
            }

            private IEnumerable<T> GetEnumerableOfType<T>(params object[] constructorArgs)
            {
                List<T> objects = new List<T>();
                foreach (Type type in
                    Assembly.GetAssembly(typeof(T)).GetTypes()
                    .Where(myType => myType.IsClass && !myType.IsAbstract && myType.IsSubclassOf(typeof(T))))
                {
                    objects.Add((T)Activator.CreateInstance(type, constructorArgs));
                }

                return objects;
            }
        }

        static void Main(string[] args)
        {
            Tester t = new Tester();


            var wssv = new WebSocketServer(IPAddress.Loopback, 54321, false);

            wssv.AddWebSocketService<Tester>("/");
            wssv.Start();

            Console.WriteLine("WS server started");

            Console.ReadKey(true);
            wssv.Stop();
        }

       
    }
}
