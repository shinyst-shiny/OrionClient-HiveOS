using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools
{
    public class Ec1ipseOrePool : OreHQPool
    {
        public override string Name { get; } = "Ec1ipse Pool [[Unofficial]]";
        public override string DisplayName => Name;
        public override bool Display => true;
        public override string Description => $"[green]{Coins}[/] pool using Ore-HQ pool implementation. 5% commission. Operators (discord): Ec1ipse | Kriptikz";
        public override Coin Coins { get; } = Coin.Ore;

        public override Dictionary<string, string> Features { get; } = new Dictionary<string, string>();

        public override bool HideOnPoolList { get; } = false;
        public override string HostName { get; protected set; } = "ec1ipse.me";

        public override Dictionary<Coin, double> MiniumumRewardPayout => new Dictionary<Coin, double> { { Coin.Ore, 0.05 } };
    }
}
