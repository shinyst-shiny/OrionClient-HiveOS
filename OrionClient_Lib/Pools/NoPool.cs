//using OrionClientLib.Hashers.Models;
//using OrionClientLib.Pools.Models;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;

//namespace OrionClientLib.Pools
//{
//    public class NoPool : IPool
//    {
//        public string PoolName { get; } = "No Pool";
//        public string DisplayName { get; } = "No Pool";
//        public string Description { get; } = "Solo mining. [red]Not recommended[/] due to transaction fees";
//        public bool HideOnPoolList { get; private set; } = false;

//        public Dictionary<string, string> Features => throw new NotImplementedException();

//        public event EventHandler<NewChallengeInfo> OnChallengeUpdate;
//        public event EventHandler<string[]> OnMinerUpdate;

//        public Task<bool> ConnectAsync(string publicKey)
//        {
//            throw new NotImplementedException();
//        }

//        public void DifficultyFound(DifficultyInfo info)
//        {
//            throw new NotImplementedException();
//        }

//        public Task<bool> DisconnectAsync()
//        {
//            throw new NotImplementedException();
//        }

//        public Task<double> GetFeeAsync()
//        {
//            throw new NotImplementedException();
//        }

//        public Task<bool> SetupAsync()
//        {
//            throw new NotImplementedException();
//        }

//        public string[] TableHeaders()
//        {
//            throw new NotImplementedException();
//        }
//    }
//}
