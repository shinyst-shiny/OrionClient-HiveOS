using Newtonsoft.Json;
using NLog;
using OrionEventLib.Events;
using System;
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

        public OrionEventHandler(int reconnectTime)
        {
            _connectTimer  = new System.Timers.Timer(TimeSpan.FromSeconds(reconnectTime));
            _connectTimer.Elapsed += _connectTimer_Elapsed;
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

        public async Task<bool> SendData(OrionEvent orionEvent)
        {
            try
            {
                if(_socket.State != WebSocketState.Open)
                {
                    return false;
                }

                byte[] data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(orionEvent));

                await _socket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);

                return true;
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Error, ex, $"Failed to send data to server");

                return false;
            }
        }
    }
}
