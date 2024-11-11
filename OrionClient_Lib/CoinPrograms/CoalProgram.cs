using NLog;
using Solnet.Programs;
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
    public class CoalProgram
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
        public static PublicKey TreasuryCoalATAId;
        public static PublicKey MintId;
        public static PublicKey ProgramId = new PublicKey("EG67mGGTxMGuPxDLWeccczVecycmpj2SokzpWeBoGVTf");
        public static readonly PublicKey NoopId = new PublicKey("noop8ytexvkpCuqbf6FB89BSuNemHtPRqaNC31GWivW");

        public static readonly double CoalDecimals = Math.Pow(10, 11);

        private static readonly byte[] MintNoise = new byte[] { 89, 157, 88, 232, 243, 249, 197, 132, 199, 49, 19, 234, 91, 94, 150, 41 };

        static CoalProgram()
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

    }
}
