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
        public bool Connected => _socket?.State == WebSocketState.Open;

        private static readonly Logger _logger = LogManager.GetLogger("Main");
        private static readonly Logger _eventLogger = LogManager.GetLogger("Events");
        private ClientWebSocket _socket;

        private string _lastUrl = String.Empty;
        private int _lastPort = 0;
        private SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private System.Timers.Timer _connectTimer;
        private System.Timers.Timer _sendTimer;
        private ConcurrentQueue<ArraySegment<byte>> _events = new ConcurrentQueue<ArraySegment<byte>>();
        private bool _enabled = false;
        private SerializationType _serializationType;

        public OrionEventHandler(bool enabled, int reconnectTime, SerializationType serialization)
        {
            _enabled = enabled;
            _serializationType = serialization;

            _connectTimer  = new System.Timers.Timer(TimeSpan.FromMilliseconds(reconnectTime));
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

                CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                _socket = new ClientWebSocket();

                string wsUrl = $"{url}:{port}";

                if(!wsUrl.StartsWith("ws"))
                {
                    wsUrl = $"ws://{wsUrl}";
                }

                await _socket.ConnectAsync(new Uri(wsUrl, UriKind.RelativeOrAbsolute), cts.Token);

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
            byte[] sharedData = ArrayPool<byte>.Shared.Rent(4096);
            ArraySegment<byte> data = null;

            if (_serializationType == SerializationType.Binary)
            {
                data = orionEvent.Serialize(new EventSerializer(new ArraySegment<byte>(sharedData)));

                _eventLogger.Log(LogLevel.Debug, Convert.ToBase64String(data));
            }
            else
            {
                var json = JsonConvert.SerializeObject(orionEvent);

                data = Encoding.UTF8.GetBytes(json);

                _eventLogger.Log(LogLevel.Debug, json);
            }

            //Prevents memory usage of queuing messages
            if (!_enabled)
            {
                //Return now rather than later
                ArrayPool<byte>.Shared.Return(sharedData);

                return;
            }

            _events.Enqueue(data);
        }

        private async Task HandleEventSend()
        {
            while(_socket?.State == WebSocketState.Open && _events.TryDequeue(out var orionEventArray))
            {
                await SendData(orionEventArray);
            }
        }

        private async Task<bool> SendData(ArraySegment<byte> orionEventArray)
        {

            bool close = false;

            try
            {
                if(_socket?.State != WebSocketState.Open)
                {
                    return false;
                }

                await _socket.SendAsync(orionEventArray, WebSocketMessageType.Text, true, CancellationToken.None);

                return true;
            }
            catch(WebSocketException ex)
            {
                close = true;
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Error, ex, $"Failed to send data to event server. Message: {ex.Message}");

                return false;
            }
            finally
            {
                if (orionEventArray.Array != null)
                {
                    ArrayPool<byte>.Shared.Return(orionEventArray.Array);
                }
            }

            if(close)
            {
                _logger.Log(LogLevel.Warn, $"Failed to send data to event server. Waiting for reconnect");

                if (_socket != null)
                {
                    try
                    {
                        _socket.Dispose();
                        _socket = null;
                    }
                    catch(Exception eX)
                    {
                        _logger.Log(LogLevel.Warn, $"Blah. {eX}");
                    }
                }
            }

            return false;
        }
    }
}
