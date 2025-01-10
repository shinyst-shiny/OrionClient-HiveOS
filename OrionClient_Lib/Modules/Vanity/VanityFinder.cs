using Chaos.NaCl;
using DrillX.Compiler;
using DrillX;
using NLog;
using Solnet.Wallet;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.Vanity
{
    internal class VanityFinder
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public int TotalWallets => VanitiesByLength.Sum(x => x.Value.Total);
        public int TotalUniqueWallets => VanitiesByLength.Sum(x => x.Value.UniqueCount);
        public int SearchVanities => VanitiesByLength.Sum(x => x.Value.Searching);
        public int InvalidCharacters { get; private set; }
        public int InvalidFormat { get; private set; }

        public ConcurrentDictionary<int, VanityTracker> VanitiesByLength { get; private set; } = new ConcurrentDictionary<int, VanityTracker>();

        //Convert b58 text to the byte values
        private byte[] _b58ToLoc;
        private VanityTree _vanityTree = new VanityTree();
        private System.Timers.Timer _saveTimer = new System.Timers.Timer(5000);
        private ConcurrentQueue<FoundVanity> _foundVanities = new ConcurrentQueue<FoundVanity>();
        private string _outputFile;

        public VanityFinder()
        {
            _b58ToLoc = new byte[256];

            for (int i = 0; i < Base58EncoderPerf.PszBase58.Length; i++)
            {
                _b58ToLoc[Base58EncoderPerf.PszBase58[i] - 49] = (byte)i;
            }

            _saveTimer.Elapsed += _saveTimer_Elapsed;
            _saveTimer.Start();
        }

        private async void _saveTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            _saveTimer.Stop();

            try
            {
                await SaveVanities();
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Warn, $"Failed to save {_foundVanities.Count} found vanities. Reason: {ex.Message}");
            }
            finally
            {
                _saveTimer.Start();
            }
        }

        public void Find(byte[] privateKey, byte[] publicKey, byte[] vanityKey, int batchSize, int threads)
        {
            if(threads <= 0)
            {
                threads = Environment.ProcessorCount;
            }

            threads = Math.Min(threads, Environment.ProcessorCount);

            int rangeSize = batchSize / threads;

            if (rangeSize == 0)
            {
                rangeSize++;
            }

            var rangePartitioner = Partitioner.Create(0, batchSize, rangeSize);

            Parallel.ForEach(rangePartitioner, new ParallelOptions { MaxDegreeOfParallelism = threads }, (range, loopState) =>
            {
                Span<byte> privKey = new Span<byte>(privateKey);
                Span<byte> pubKey = new Span<byte>(publicKey);
                Span<byte> vanKey = new Span<byte>(vanityKey);

                string potentialVanity = String.Empty;

                byte[] output = new byte[64];

                // Loop over each range element without a delegate invocation.
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    potentialVanity = String.Empty;

                    int index = i * 32;

                    Span<byte> currentVanity = vanKey.Slice(index, 32);

                    Node currentNode = _vanityTree.GetNode(currentVanity[0]);

                    if (currentNode != null)
                    {
                        for (int z = 1; z < currentVanity.Length; z++)
                        {
                            currentNode = currentNode.GetNode(currentVanity[z]);

                            if (currentNode == null)
                            {
                                break;
                            }

                            if(!String.IsNullOrEmpty(currentNode.Vanity))
                            {
                                potentialVanity = currentNode.Vanity;
                            }
                        }
                    }

                    if (!String.IsNullOrEmpty(potentialVanity))
                    {
                        FoundVanity foundVanity = new FoundVanity
                        {
                            PrivateKey = EncodeBytes(privKey.Slice(index, 32), output),
                            PublicKey = EncodeBytes(pubKey.Slice(index, 32), output),
                            VanityText = potentialVanity,
                        };


                        var r = VanitiesByLength.AddOrUpdate(foundVanity.VanityText.Length, (k) => new VanityTracker { VanityLength = foundVanity.VanityText.Length }, (k, v) => v);
                        r.Add(foundVanity);

                        _foundVanities.Enqueue(foundVanity);

                        //byte[] pubKeyActual = Ed25519.ExpandedPrivateKeyFromSeed(privKey.Slice(i * 32, 32).ToArray())[32..64];
                        //Span<byte> pubKeyFound = pubKey.Slice(i * 32, 32);

                        //byte[] output = new byte[64];

                        //Base58EncoderPerf.Encode(pubKeyActual, 0, output, out int s, out int end);

                        //if(!pubKeyFound.SequenceEqual(pubKeyActual))
                        //{

                        //}

                        //string test = Encoding.UTF8.GetString(output, s, end);

                        [MethodImpl(MethodImplOptions.AggressiveInlining)]
                        string EncodeBytes(Span<byte> bytes, byte[] buffer)
                        {
                            Base58EncoderPerf.Encode(bytes, 0, buffer, out int s, out int end);

                            return Encoding.UTF8.GetString(buffer, s, end);
                        }
                    }
                }
            });
        }

        public async Task<(bool success, string message)> Load(string inputFile, string outputFile, string walletDirectory, int minimumCharacterLength)
        {
            VanitiesByLength.Clear();
            _vanityTree.Clear();
            InvalidCharacters = 0;
            InvalidFormat = 0;
            _outputFile = outputFile;

            Directory.CreateDirectory(walletDirectory);

            if(File.Exists(outputFile))
            {
                string[] lines = await File.ReadAllLinesAsync(outputFile);

                HashSet<string> exportedWallets = new HashSet<string>(Directory.GetFiles(walletDirectory, "*.json").Select(x => x.Split('\\', StringSplitOptions.RemoveEmptyEntries).Last().Split('.').First()));

                foreach (string line in lines)
                {
                    string[] parts = line.Split(":", StringSplitOptions.RemoveEmptyEntries);

                    if(parts.Length != 4)
                    {
                        continue;
                    }

                    var r = VanitiesByLength.AddOrUpdate(parts[1].Length, (k) => new VanityTracker { VanityLength = parts[1].Length }, (k, v) => v);
                    r.Add(new FoundVanity
                    {
                        VanityText = parts[1],
                        PublicKey = parts[2],
                        PrivateKey = parts[3],
                        Exported = exportedWallets.Contains(parts[2])
                    });
                }
            }
            else
            {
                using var _ = File.Create(outputFile);
            }

            if(File.Exists(inputFile))
            {
                string[] lines = await File.ReadAllLinesAsync(inputFile);

                HashSet<char> validChars = new HashSet<char>(Base58EncoderPerf.PszBase58);

                foreach (string line in lines)
                {
                    string vanity = line;

                    if(String.IsNullOrEmpty(vanity))
                    {
                        ++InvalidFormat;

                        continue;
                    }

                    bool isValid = true;

                    foreach(char c in vanity)
                    {
                        if(!validChars.Contains(c))
                        {
                            ++InvalidCharacters;
                            isValid = false;
                            break;
                        }
                    }

                    if(vanity.Length < minimumCharacterLength)
                    {
                        continue;
                    }

                    //Add
                    if(isValid)
                    {
                        char character = vanity[0];

                        Node node = _vanityTree.GetNode(_b58ToLoc[character - 49], true, character);

                        for(int i = 1; i < vanity.Length; i++)
                        {
                            character = vanity[i];

                            node = node.GetNode(_b58ToLoc[character - 49], true, character);
                        }

                        if(String.IsNullOrEmpty(node.Vanity))
                        {
                            node.Vanity = vanity;

                            //Add to properly display the UI
                            var r = VanitiesByLength.AddOrUpdate(vanity.Length, (k) => new VanityTracker(), (k, v) => v);
                            r.Searching++;
                        }
                    }
                }
            }
            else
            {
                using var _ = File.Create(inputFile);
            }

            return (true, String.Empty);
        }

        private async Task SaveVanities()
        {
            List<FoundVanity> toSave = new List<FoundVanity>(_foundVanities.Count);

            while(_foundVanities.TryDequeue(out var result))
            {
                toSave.Add(result);
            }

            if(toSave.Count == 0)
            {
                return;
            }

            try
            {
                await File.AppendAllLinesAsync(_outputFile, toSave.Select(x => $"{x.VanityText.Length}:{x.VanityText}:{x.PublicKey}:{x.PrivateKey}"));
            }
            catch
            {
                //Readd
                toSave.ForEach(_foundVanities.Enqueue);

                throw;
            }
        }

        private class VanityTree
        {
            public Node[] Nodes { get; private set; } = new Node[64];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Node GetNode(byte c)
            {
                return Nodes[c];

            }

            public Node GetNode(byte c, bool add, char character)
            {
                Node node = Nodes[c];

                if(node == null && add)
                {
                    node = new Node(character);
                    Nodes[c] = node;
                }

                return node;
            }

            public void Clear()
            {
                Array.Clear(Nodes);
            }
        }

        private class Node
        {
            public Node[] Nodes { get; private set; } = new Node[64];
            public string Vanity { get; set; } = String.Empty;
            public char Character { get; private set; }

            public Node(char character)
            {
                Character = character;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Node GetNode(byte c)
            {
                return Nodes[c];
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Node GetNode(byte c, bool add, char character)
            {
                Node node = Nodes[c];

                if (node == null && add)
                {
                    node = new Node(character);
                    Nodes[c] = node;
                }

                return node;
            }
        }
    }
}
