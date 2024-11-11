using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools
{
    public class ShinystCoalPool : OreHQPool
    {
        public override string PoolName { get; } = "Coal Pool";
        public override string DisplayName => PoolName;
        public override string Description => $"[Cyan]{Coins}[/] pool using Ore-HQ implementation. Operator (discord): Shinyst";
        public override Coin Coins { get; } = Coin.Coal;

        public override Dictionary<string, string> Features { get; } = new Dictionary<string, string>();

        public override bool HideOnPoolList { get; } = false;
        public override string HostName { get; protected set; } = "pool.coal-pool.xyz";
    }
}
