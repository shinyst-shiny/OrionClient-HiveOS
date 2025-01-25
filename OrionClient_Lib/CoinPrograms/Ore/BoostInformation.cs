using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.CoinPrograms.Ore
{
    public class BoostInformation
    {
        public enum PoolType { Unknown, Ore, Meteroa, Kamino };

        public PublicKey BoostAddress { get; private set; }
        public PublicKey MintAddress { get; private set; }
        public PublicKey PoolAddress { get; private set; }
        public PublicKey CheckpointAddress { get; private set; }
        public PublicKey BoostProof { get; private set; }

        public int Decimal { get; private set; }
        public string Name { get; private set; }
        public PoolType Type { get; private set; }

        public BoostInformation(PublicKey mintAddress, int decimals, string name, PoolType type, PublicKey poolAddress)
        {
            MintAddress = mintAddress;
            BoostAddress = OreProgram.DeriveBoost(MintAddress);
            CheckpointAddress = OreProgram.DeriveCheckpoint(BoostAddress);
            BoostProof = OreProgram.GetProofKey(BoostAddress, OreProgram.ProgramId).key;

            Name = name;
            Decimal = decimals;
            PoolAddress = poolAddress;
            Type = type;
        }
    }
}
