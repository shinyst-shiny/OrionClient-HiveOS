//using OrionClientLib.Hashers.Models;
//using OrionClientLib.Pools.Models;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Net.WebSockets;
//using System.Text;
//using System.Threading.Tasks;

//namespace OrionClientLib.Pools
//{
//    public abstract class WebsocketPool : IPool
//    {
//        public abstract string PoolName { get; }
//        public abstract string DisplayName { get; }
//        public abstract string Description { get; }

//        public abstract Dictionary<string, string> Features { get; }

//        public abstract bool HideOnPoolList { get; }

//        public event EventHandler<NewChallengeInfo> OnChallengeUpdate;
//        public event EventHandler<MinerTableInformation> OnMinerUpdate;

//        protected ClientWebSocket _webSocket;

//        public async Task<bool> ConnectAsync()
//        {
//            return false;
//        }


//        public async Task<bool> DisconnectAsync()
//        {
//            return false;
//        }

//        public abstract void DifficultyFound(DifficultyInfo info);
//        public abstract Task<double> GetFeeAsync();
//        public abstract Task<bool> SetupAsync();
//    }
//}
