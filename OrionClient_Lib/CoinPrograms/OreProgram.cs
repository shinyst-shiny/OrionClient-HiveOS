using NLog;
using OrionClientLib.CoinPrograms.Ore;
using Solnet.Programs;
using Solnet.Programs.Abstract;
using Solnet.Programs.Utilities;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.CoinPrograms
{
    public class OreProgram
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private static readonly PublicKey SlotHashesKey = new("SysvarS1otHashes111111111111111111111111111");
        private static readonly PublicKey Instructions = new("Sysvar1nstructions1111111111111111111111111");


        public enum Errors
        {
            Unknown = -1,
            NeedsReset = 0,
            HashInvalid = 1,
            HashTooEasy = 2,
            ClaimTooLarge = 3,
            ClockInvalid = 4,
            Spam = 5,
            MaxSupply = 6,
            AuthFailed = 7,
            MiningDisabled = 8,


            BlockhashNotFound = 1000
        };

        private enum Instruction { Claim = 0, Close, Mine, Open, Reset, Stake, Update, Upgrade, Initialize = 100 };

        public static readonly PublicKey[] BusIds = new PublicKey[8];

        public static PublicKey ConfigAddress;
        public static PublicKey TreasuryId;
        public static PublicKey TreasuryATAId;
        public static PublicKey TreasuryOreATAId;
        public static PublicKey MintId;
        public static PublicKey ProgramId = new PublicKey("oreV2ZymfyeXgNgBdqMkumTqqAprVqgBWQfoYkrtKWQ");
        public static readonly PublicKey NoopId = new PublicKey("noop8ytexvkpCuqbf6FB89BSuNemHtPRqaNC31GWivW");
        public static readonly PublicKey BoostProgramId = new PublicKey("BoosTyJFPPtrqJTdi49nnztoEWDJXfDRhyb2fha6PPy");

        public static readonly List<BoostInformation> Boosts = new List<BoostInformation>()
        {
            new BoostInformation(new PublicKey("oreoU2P8bN6jkk3jbaiVxYnG1dCXcYxwhwyK9jSybcp"), 11, "Ore", BoostInformation.PoolType.Ore, null),
            new BoostInformation(new PublicKey("8H8rPiWW4iTFCfEkSnf7jpqeNpFfvdH9gLouAL3Fe2Zx"), 6, "Ore-Sol (Kamino)", BoostInformation.PoolType.Kamino,new PublicKey("6TFdY15Mxty9sRCtzMXG8eHSbZy4oiAEQUvLQdz9YwEn")),
            new BoostInformation(new PublicKey("7G3dfZkSk1HpDGnyL37LMBbPEgT4Ca6vZmZPUyi2syWt"), 6, "Ore-Hnt (Kamino)", BoostInformation.PoolType.Kamino,new PublicKey("9XsAPjk1yp4U6hKZj9r9szhcxBi3RidGuyxiC2Y8JtAe")),
            new BoostInformation(new PublicKey("DrSS5RM7zUd9qjUEdDaf31vnDUSbCrMto6mjqTrHFifN"), 11, "Ore-Sol (Meteroa)", BoostInformation.PoolType.Meteroa,new PublicKey("GgaDTFbqdgjoZz3FP7zrtofGwnRS4E6MCzmmD5Ni1Mxj")),
            new BoostInformation(new PublicKey("meUwDp23AaxhiNKaQCyJ2EAF2T4oe1gSkEkGXSRVdZb"), 11, "Ore-ISC (Meteroa)", BoostInformation.PoolType.Meteroa,new PublicKey("2vo5uC7jbmb1zNqYpKZfVyewiQmRmbJktma4QHuGNgS5")),
        };


        public static readonly double OreDecimals = Math.Pow(10, 11);
        private static readonly byte[] MintNoise = new byte[] { 89, 157, 88, 232, 243, 249, 197, 132, 199, 49, 19, 234, 91, 94, 150, 41 };

        static OreProgram()
        {
            Initialize(ProgramId);
        }

        public static void Initialize(PublicKey programId)
        {
            ProgramId = programId;

            //Generate busses
            for (int i = 0; i < BusIds.Length; i++)
            {
                PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("bus"), new byte[] { (byte)i } }, ProgramId, out var publicKey, out byte nonce);

                BusIds[i] = publicKey;
            }

            PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("treasury") }, ProgramId, out var b, out var n);
            TreasuryId = b;

            PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("mint"), MintNoise }, ProgramId, out b, out n);
            MintId = b;


            PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("config") }, ProgramId, out b, out n);
            ConfigAddress = b;

            TreasuryATAId = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(TreasuryId, MintId);
        }

        public static (PublicKey key, uint nonce) GetProofKey(PublicKey signer, PublicKey programId)
        {
            if (PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("proof"), signer.KeyBytes }, programId, out PublicKey address, out byte nonce))
            {
                return (address, nonce);
            }

            return default;
        }

        public static TransactionInstruction Register(PublicKey programId, PublicKey signer, PublicKey minerAuthority, PublicKey fundingWallet, PublicKey systemProgram, PublicKey slotHashes)
        {
            var proof = GetProofKey(signer, programId);

            List<AccountMeta> keys = new()
            {
                AccountMeta.Writable(signer, true),
                AccountMeta.ReadOnly(minerAuthority, false),
                AccountMeta.Writable(fundingWallet, true),
                AccountMeta.Writable(proof.key, false),
                AccountMeta.ReadOnly(systemProgram, false),
                AccountMeta.ReadOnly(slotHashes, false),
            };

            byte[] data = new byte[2];
            data[0] = (byte)Instruction.Open;
            data.WriteU8((byte)proof.nonce, 1);

            return new TransactionInstruction
            {
                ProgramId = programId,
                Keys = keys,
                Data = data
            };
        }

        public static TransactionInstruction Close(PublicKey programId, PublicKey signer, PublicKey systemProgram)
        {
            var proof = GetProofKey(signer, programId);

            List<AccountMeta> keys = new()
            {
                AccountMeta.Writable(signer, true),
                AccountMeta.Writable(proof.key, false),
                AccountMeta.ReadOnly(systemProgram, false),
            };

            byte[] data = new byte[1];
            data[0] = (byte)Instruction.Close;

            return new TransactionInstruction
            {
                ProgramId = programId,
                Keys = keys,
                Data = data
            };
        }

        public static TransactionInstruction Mine(PublicKey programId, PublicKey signer, PublicKey bus, PublicKey proof, byte[] solution, ulong nonce)
        {
            List<AccountMeta> keys = new()
            {
                AccountMeta.Writable(signer, true),
                AccountMeta.Writable(bus, false),
                AccountMeta.ReadOnly(ConfigAddress, false),
                AccountMeta.Writable(proof, false),
                AccountMeta.ReadOnly(Instructions, false),
                AccountMeta.ReadOnly(SlotHashesKey, false),
            };

            byte[] data = new byte[25];
            data[0] = (byte)Instruction.Mine;
            data.WriteSpan(solution, 1); //16 bytes
            data.WriteU64(nonce, 17); //8 bytes

            return new TransactionInstruction
            {
                ProgramId = programId,
                Keys = keys,
                Data = data
            };
        }

        public static TransactionInstruction Claim(PublicKey programId, PublicKey signer, PublicKey beneficiary, PublicKey proof, PublicKey teasury, PublicKey teasuryATA, PublicKey splTokenProgram, ulong claimAmount)
        {
            List<AccountMeta> keys = new()
            {
                AccountMeta.Writable(signer, true),
                AccountMeta.Writable(beneficiary, false),
                AccountMeta.Writable(proof, false),
                AccountMeta.ReadOnly(teasury, false),
                AccountMeta.Writable(teasuryATA, false),
                AccountMeta.ReadOnly(splTokenProgram, false),
            };

            byte[] data = new byte[9];
            data[0] = (byte)Instruction.Claim;
            data.WriteU64(claimAmount, 1);

            return new TransactionInstruction
            {
                ProgramId = programId,
                Keys = keys,
                Data = data
            };
        }

        public static TransactionInstruction Auth(PublicKey proof)
        {
            List<AccountMeta> keys = new();

            byte[] data = proof.KeyBytes;

            return new TransactionInstruction
            {
                ProgramId = NoopId,
                Keys = keys,
                Data = data
            };
        }

        public static PublicKey DeriveBoost(PublicKey mint)
        {
            if (PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("boost"), mint.KeyBytes }, BoostProgramId, out PublicKey address, out byte nonce))
            {
                return address;
            }

            return default;
        }

        public static PublicKey DeriveCheckpoint(PublicKey boost)
        {
            if (PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("checkpoint"), boost.KeyBytes }, BoostProgramId, out PublicKey address, out byte nonce))
            {
                return address;
            }

            return default;
        }

        public static PublicKey DeriveStakeAccount(PublicKey boost, PublicKey authority)
        {
            if (PublicKey.TryFindProgramAddress(new List<byte[]> { Encoding.UTF8.GetBytes("stake"), authority.KeyBytes, boost.KeyBytes }, BoostProgramId, out PublicKey address, out byte nonce))
            {
                return address;
            }

            return default;
        }

    }
}
