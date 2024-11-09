using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools
{
    public class ShinystPool : OreHQPool
    {
        public override string PoolName { get; } = "Shinyst [[Unofficial]]";
        public override string DisplayName => PoolName;
        public override string Description { get; } = "Unofficial pool implemented provided by Shinyst. 5% commission";

        public override Dictionary<string, string> Features { get; } = new Dictionary<string, string>();

        public override bool HideOnPoolList { get; } = false;
        public override string HostName { get; } = "pool.coal-pool.xyz";
    }
}
