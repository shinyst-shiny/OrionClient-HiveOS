using Newtonsoft.Json;
using NLog;
using OrionEventLib.Events;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace OrionEventLib
{
    public class OrionEventHandler
    {
        public event EventHandler OnReconnect;

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private ClientWebSocket _socket;

        private string _lastUrl = String.Empty;
        private int _lastPort = 0;
        private SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private System.Timers.Timer _connectTimer;
        private System.Timers.Timer _sendTimer;
        private ConcurrentQueue<OrionEvent> _events = new ConcurrentQueue<OrionEvent>();
        private bool _enabled = false;
        private SerializationType _serializationType;

        public OrionEventHandler(bool enabled, int reconnectTime, SerializationType serialization)
        {
            _enabled = enabled;
            _serializationType = serialization;

            _connectTimer  = new System.Timers.Timer(TimeSpan.FromSeconds(reconnectTime));
            _connectTimer.Elapsed += _connectTimer_Elapsed;
            _sendTimer = new System.Timers.Timer(TimeSpan.FromSeconds(1));
            _sendTimer.Elapsed += _sendTimer_Elapsed;

            if(_enabled)
            {
                _connectTimer.Start();
                _sendTimer.Start();
            }
        }

        private async void _sendTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                _sendTimer.Stop();

                await HandleEventSend();
            }
            finally
            {
                _sendTimer.Start();
            }
        }

        private async void _connectTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                _connectTimer.Stop();

                await Connect(_lastUrl, _lastPort, false);
            }
            finally
            {
                _connectTimer.Start();
            }
        }

        public async Task<bool> Connect(string url, int port, bool firstConnect = true)
        {
            if(String.IsNullOrEmpty(url))
            {
                return false;
            }

            if(firstConnect)
            {
                _lastUrl = url;
                _lastPort = port;
            }

            try
            {
                await _connectLock.WaitAsync();

                if (_socket?.State == WebSocketState.Open)
                {
                    return true;
                }

                CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));

                _socket = new ClientWebSocket();
                await _socket.ConnectAsync(new Uri($"{url}:{port}", UriKind.RelativeOrAbsolute), cts.Token);

                if(!firstConnect)
                {
                    OnReconnect?.Invoke(this, EventArgs.Empty);
                }

                _logger.Log(LogLevel.Info, $"Successfully connected to event server");
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Warn, $"Failed to connect to event server. Message: {ex.Message}");
            }
            finally
            {
                _connectLock.Release();
            }

            return false;
        }

        public void AddEvent(OrionEvent orionEvent)
        {
            //Prevents memory usage of queuing messages
            if(!_enabled)
            {
                return;
            }

            _events.Enqueue(orionEvent);
        }

        private async Task HandleEventSend()
        {
            while(_socket?.State == WebSocketState.Open && _events.TryDequeue(out OrionEvent orionEvent))
            {
                await SendData(orionEvent);
            }
        }

        private async Task<bool> SendData(OrionEvent orionEvent)
        {
            byte[] sharedData = ArrayPool<byte>.Shared.Rent(4096);

            try
            {
                if(_socket.State != WebSocketState.Open)
                {
                    return false;
                }

                ArraySegment<byte> data = null;

                if(_serializationType == SerializationType.Binary)
                {
                    data = orionEvent.Serialize(new EventSerializer(new ArraySegment<byte>(sharedData)));
                }
                else
                {
                    data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(orionEvent));
                }

                await _socket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);

                return true;
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Error, ex, $"Failed to send data to server");

                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sharedData);
            }
        }
    }
}
