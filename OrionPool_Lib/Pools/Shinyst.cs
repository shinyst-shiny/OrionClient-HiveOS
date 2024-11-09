using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools
{
    public class ShinystPool : OreHQPool
    {
        public override string PoolName { get; } = "Shinyst Pool";
        public override string DisplayName => PoolName;
        public override string Description => $"[Cyan]{Coins}[/] pool using Ore-HQ implementation";
        public override Coin Coins { get; } = Coin.Coal;

        public override Dictionary<string, string> Features { get; } = new Dictionary<string, string>();

        public override bool HideOnPoolList { get; } = false;
        public override string HostName { get; } = "pool.coal-pool.xyz";
    }
}
