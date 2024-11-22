using OrionClientLib.Modules.Models;
using OrionClientLib.Pools.Models;
using Solnet.Wallet;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools.CoalPool
{
    internal class CoalHQPoolSubmissionResponse : IMessage
    {
        public uint Difficulty { get; private set; }
        public byte[] Challenge { get; private set; }
        public ulong BestNonce { get; private set; }
        public uint ActiveMiners { get; private set; }
        public CoalDetails CoalDetail { get; private set; } = new CoalDetails();
        public OreDetails OreDetail { get; private set; } = new OreDetails();

        public void Deserialize(ArraySegment<byte> data)
        {
            DataReader reader = new DataReader(data.Slice(1));

            Difficulty = reader.ReadUInt();
            Challenge = reader.ReadBytes(32);
            BestNonce = reader.ReadULong();
            ActiveMiners = reader.ReadUInt();
            CoalDetail.Parse(reader);
            OreDetail.Parse(reader);
        }

        public ArraySegment<byte> Serialize()
        {
            throw new NotImplementedException();
        }

        public class RewardDetails
        {
            public double TotalBalance { get; private set; }
            public double TotalRewards { get; private set; }
            public uint MinerSuppliedDifficulty { get; private set; }
            public double MinerEarnedRewards { get; private set; }
            public double MinerPercentage { get; private set; }
            public double TopStake { get; private set; }
            public double StakeMultiplier { get; private set; }

            public void Parse(DataReader data)
            {
                TotalBalance = data.ReadDouble();
                TotalRewards = data.ReadDouble();
                MinerSuppliedDifficulty = data.ReadUInt();
                MinerEarnedRewards = data.ReadDouble();
                MinerPercentage = data.ReadDouble();
                TopStake = data.ReadDouble();
                StakeMultiplier = data.ReadDouble();
            }
        }

        public class CoalDetails
        {
            public RewardDetails RewardDetails { get; private set; } = new RewardDetails();
            public double GuildTotalStake { get; private set; }
            public double GuildMultiplier { get; private set; }
            public double ToolMultiplier { get; private set; }

            public void Parse(DataReader data)
            {
                RewardDetails.Parse(data);
                GuildTotalStake = data.ReadDouble();
                GuildMultiplier = data.ReadDouble();
                ToolMultiplier = data.ReadDouble();
            }
        }


        public class OreDetails
        {
            public RewardDetails RewardDetails { get; private set; } = new RewardDetails();
            public List<OreBoost> OreBoosts { get; private set; } = new List<OreBoost>();

            public void Parse(DataReader data)
            {
                RewardDetails.Parse(data);

                uint totalBoosts = data.ReadUInt();

                for(int i = 0; i < totalBoosts; i++)
                {
                    OreBoost boost = new OreBoost();
                    boost.Parse(data);

                    OreBoosts.Add(boost);
                }
            }
        }

        public class OreBoost
        {
            public double TopStake { get; private set; }
            public double TotalStake { get; private set; }
            public double StakeMultiplier { get; private set; }
            public PublicKey MintAddress { get; private set; }
            public string Name { get; private set; }

            public void Parse(DataReader data)
            {
                TopStake = data.ReadDouble();
                TotalStake = data.ReadDouble();
                StakeMultiplier = data.ReadDouble();
                MintAddress = data.ReadPublicKey();

                //Don't have anything to test this against
                //var test = data.ReadUInt();

                //data.ReadBytes((int)test);
            }
        }
    }

}
