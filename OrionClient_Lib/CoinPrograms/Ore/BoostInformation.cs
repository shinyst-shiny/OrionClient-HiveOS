using Solnet.Programs.Utilities;
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
        public enum PoolType { Unknown, Ore, Meteora, Kamino };

        public PublicKey BoostAddress { get; private set; }
        public PublicKey MintAddress { get; private set; }
        public PublicKey PoolAddress { get; private set; }
        public PublicKey CheckpointAddress { get; private set; }
        public PublicKey BoostProof { get; private set; }


        public int Decimal { get; private set; }
        public string Name { get; private set; }
        public PoolType Type { get; private set; }
        public string CoinGeckoName { get; private set; }

        public MeteoraExtraData ExtraData { get; private set; }

        public BoostInformation(string mintAddress, int decimals, string name, PoolType type, string poolAddress, string coinGeckoName, MeteoraExtraData extraData = null)
        {
            MintAddress = new PublicKey(mintAddress);
            BoostAddress = OreProgram.DeriveBoost(MintAddress);
            CheckpointAddress = OreProgram.DeriveCheckpoint(BoostAddress);
            BoostProof = OreProgram.GetProofKey(BoostAddress, OreProgram.ProgramId).key;
            ExtraData = extraData;
            Name = name;
            Decimal = decimals;
            PoolAddress = poolAddress == null ? null : new PublicKey(poolAddress);
            Type = type;
            CoinGeckoName = coinGeckoName;
        }

        public class MeteoraExtraData
        {
            public const int TotalAccounts = 6;

            public PublicKey LPVaultA { get; private set; }
            public PublicKey LPVaultB { get; private set; }
            public PublicKey TokenVaultA { get; private set; }
            public PublicKey TokenVaultB { get; private set; }
            public PublicKey LPAMint { get; private set; }
            public PublicKey LPBMint { get; private set; }

            public int TokenBDecimal { get; private set; }

            public MeteoraExtraData(string lpVaultA, string lpVaultB, string tokenVaultA, string tokenVaultB, string lpAMint, string lpBMint, int tokenBDecimal)
            {
                LPVaultA = new PublicKey(lpVaultA);
                LPVaultB = new PublicKey(lpVaultB);
                TokenVaultA = new PublicKey(tokenVaultA);
                TokenVaultB = new PublicKey(tokenVaultB);
                LPAMint = new PublicKey(lpAMint);
                LPBMint = new PublicKey(lpBMint);
                TokenBDecimal = tokenBDecimal;
            }

            public ulong CalculateTokenAmount(ulong tokenAmount, ulong mintAmount, ReadOnlySpan<byte> vaultData)
            {
                //for(int i =0; i < vaultData.Length - 8; i++)
                //{
                //    if (vaultData.GetU64(i) > 0)
                //    {
                //        Console.WriteLine($"{i} = {vaultData.GetU64(i) / 100000000000.0}");
                //    }
                //}

                ulong vaultAmount = vaultData.GetU64(11);

                //Locked profit info
                var lastUpdatedLockedProfit = vaultData.GetU64(1203);
                var lastReport = vaultData.GetU64(1211);
                var degradation = vaultData.GetU64(1219);


                var duration = new UInt128(0, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastReport);
                var lockedProfitDegradation = new UInt128(0, degradation);
                var lockedFundRatio = duration * lockedProfitDegradation;

                if (lockedFundRatio > 1_000_000_000_000)
                {
                    return tokenAmount;
                }

                var lockedProfit = new UInt128(0, lastUpdatedLockedProfit);
                lockedProfit = lockedProfit * (1_000_000_000_000 - lockedFundRatio) / 1_000_000_000_000;

                return (ulong)(new UInt128(0, tokenAmount) * new UInt128(0, vaultAmount) / (mintAmount - (ulong)lockedProfit));

                //LP * Vault / tttt

                return 0;
            }
        }
    }
}
