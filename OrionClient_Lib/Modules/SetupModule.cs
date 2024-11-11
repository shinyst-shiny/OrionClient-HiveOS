using Equix;
using Hardware.Info;
using Newtonsoft.Json;
using OrionClientLib.Hashers;
using OrionClientLib.Modules.Models;
using OrionClientLib.Pools;
using OrionClientLib.Utilities;
using Solnet.Wallet;
using Solnet.Wallet.Bip39;
using Solnet.Wallet.Utilities;
using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules
{
    public class SetupModule : IModule
    {
        public string Name { get; } = "Run Setup";

        private int _currentStep = 0;
        private List<Func<Task<int>>> _steps = new List<Func<Task<int>>>();
        private Data _data;
        private Settings _settings => _data?.Settings;
        private CancellationTokenSource _cts;
        private string _errorMessage = String.Empty;

        public SetupModule()
        {
            _steps.Add(WalletSetupAsync);
            _steps.Add(ChooseCPUHasherAsync);
            _steps.Add(ThreadCountAsync);
            _steps.Add(ChoosePoolAsync);
            _steps.Add(FinalConfirmationAsync);
        }

        public async Task<ExecuteResult> ExecuteAsync(Data data)
        {
            return new ExecuteResult
            {
                Exited = true
            };
        }

        public async Task ExitAsync()
        {
            _cts.Cancel();
        }

        public async Task<(bool, string)> InitializeAsync(Data data)
        {
            _cts = new CancellationTokenSource();
            _currentStep = 0;
            _data = data;

            bool reloadSettings = false;

            try
            {
                while (_currentStep < _steps.Count)
                {
                    _currentStep = await _steps[_currentStep]();
                }
            }
            catch(TaskCanceledException)
            {
                reloadSettings = true;
            }

            if(reloadSettings)
            {
                await _settings.ReloadAsync();

                return (false, "Setup cancelled by user");
            }

            return (true, String.Empty);
        }

        private async Task<int> WalletSetupAsync()
        {
            while (true)
            {
                (Wallet solanaWallet, string publicKey) = await _settings.GetWalletAsync();

                SelectionPrompt<string> selectionPrompt = new SelectionPrompt<string>();
                selectionPrompt.Title($"Setup solana wallet. Current: {publicKey ?? "??"}. Path: {(_settings.HasPrivateKey ? (_settings.KeyFile ?? "N/A") : "N/A")}");

                if (!String.IsNullOrEmpty(publicKey))
                {
                    selectionPrompt.AddChoice("Confirm");
                }

                selectionPrompt.EnableSearch();

                selectionPrompt.AddChoice("Use Public Key");
                selectionPrompt.AddChoice("Create New");
                selectionPrompt.AddChoice("Search");
                selectionPrompt.AddChoice("Exit");

                string response = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

                switch (response.ToLower())
                {
                    case "confirm":
                        return _currentStep + 1;
                    case "create new":
                        await CreateNewWallet(solanaWallet);
                        break;
                    case "search":
                        await SearchWallet();
                        break;
                    case "use public key":
                        await SetupPublicKey(publicKey);
                        break;
                    case "exit":
                        return _steps.Count;
                }
            }
        }

        private async Task<int> ChooseCPUHasherAsync()
        {
            (IHasher chosenHasher, IHasher gpuHasher) = _data.GetChosenHasher();

            SelectionPrompt<IHasher> selectionPrompt = new SelectionPrompt<IHasher>();
            selectionPrompt.Title("Select hashing implementation. Run benchmark to see hashrates");
            selectionPrompt.UseConverter((pool) =>
            {
                string chosenText = String.Empty;

                if (pool == chosenHasher)
                {
                    chosenText = "[b][[Current]][/] ";
                }

                return $"{chosenText}{pool.Name} - {pool.Description}";
            });

            selectionPrompt.AddChoices(_data.Hashers.Where(x => x.HardwareType == IHasher.Hardware.CPU).OrderByDescending(x =>x == chosenHasher));

            chosenHasher = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);


            _settings.CPUHasher = chosenHasher.Name;

            return _currentStep + 1;
        }

        private async Task<int> ThreadCountAsync()
        {
            List<CoreInfo> coreInfo = SystemInformation.GetCoreInformation();

            List<(int, string)> choices = new List<(int, string)>();

            int totalThreads = Environment.ProcessorCount;

            if (coreInfo.Count == 0)
            {
                choices.Add((Environment.ProcessorCount, "(100% usage) [green]Recommended[/]"));
                choices.Add((1, "(single thread)"));
                choices.Add((0, "Custom"));
            }
            else
            {
                totalThreads = coreInfo.Sum(x => x.ThreadCount);
                bool hasECores = coreInfo.Any(x => !x.IsPCore);


                choices.Add((totalThreads, "(100% usage) [green]Recommended[/]"));

                if (coreInfo.Count != totalThreads)
                {
                    choices.Add((coreInfo.Count, "(physical cores only)"));
                }
                //CPU has efficiency cores
                if (hasECores)
                {
                    List<CoreInfo> pCores = coreInfo.Where(x => x.IsPCore).ToList();
                    int totalPerformanceThreads = pCores.Sum(x => x.ThreadCount);

                    if (coreInfo.Count == totalThreads)
                    {
                        choices.Add((pCores.Sum(x => x.ThreadCount), "(performance cores only)"));
                    }

                    if (pCores.Count != totalPerformanceThreads)
                    {
                        choices.Add((pCores.Count, "(physical performance cores only)"));
                    }
                }

                choices.Add((1, "(single thread)"));
                choices.Add((0, "Custom"));

                if (!choices.Any(x => x.Item1 == _settings.CPUThreads))
                {
                    choices.Add((_settings.CPUThreads, "[[Current]]"));
                }
            }

            SelectionPrompt<(int, string)> selectionPrompt = new SelectionPrompt<(int, string)>();
            selectionPrompt.Title($"Select total threads. Highest value recommended. Current: {_settings.CPUThreads}");
            selectionPrompt.UseConverter((tuple) =>
            {
                if(tuple.Item1 > 0)
                {
                    return $"{tuple.Item1} {tuple.Item2}";
                }

                return tuple.Item2;
            });

            selectionPrompt.AddChoices(choices.OrderByDescending(x => x.Item1 == _settings.CPUThreads).ThenByDescending(x => x.Item1));

            (int, string) choice = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);
            
            if(choice.Item1 == 0)
            {
                while (true)
                {
                    TextPrompt<int> textPrompt = new TextPrompt<int>($"Total threads (min: 1, max: {totalThreads}):");
                    textPrompt.DefaultValue(_settings.CPUThreads);

                    int result = await textPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

                    AnsiConsole.Clear();

                    if (result > 0 && result <= totalThreads)
                    {
                        _settings.CPUThreads = result;
                        break;
                    }
                }
            }
            else
            {
                _settings.CPUThreads = choice.Item1;
            }

            return _currentStep + 1;
        }

        private async Task<int> ChoosePoolAsync()
        {
            IPool chosenPool = _data.GetChosenPool();
            
            SelectionPrompt<IPool> selectionPrompt = new SelectionPrompt<IPool>();
            selectionPrompt.Title($"Pool Selection{(!String.IsNullOrEmpty(_errorMessage) ? $"\n[red]Error: {_errorMessage}[/]\n" : String.Empty)}");
            _errorMessage = String.Empty;

            selectionPrompt.UseConverter((pool) =>
            {
                string chosenText = String.Empty;

                if(pool == chosenPool)
                {
                    chosenText = "[b][[Current]][/] ";
                }

                if (pool == null)
                {
                    return $"{chosenText}Nothing - Skips pool selection";
                }

                return $"{chosenText}{pool.DisplayName} - {pool.Description}";
            });

            if(chosenPool == null)
            {
                selectionPrompt.AddChoice(null);
                selectionPrompt.AddChoices(_data.Pools);
            }
            else
            {
                selectionPrompt.AddChoices(_data.Pools.OrderByDescending(x => x == chosenPool));
                selectionPrompt.AddChoice(null);
            }


            chosenPool = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            if(chosenPool != null)
            {
                (Wallet wallet, string publicKey) = await _data.Settings.GetWalletAsync();

                chosenPool.SetWalletInfo(wallet, publicKey);

                var poolSetup = await chosenPool.SetupAsync(_cts.Token, true);

                if (!poolSetup.success)
                {
                    _errorMessage = poolSetup.errorMessage;

                    return _currentStep;
                }

                AnsiConsole.Clear();
            }

            _settings.Pool = chosenPool?.PoolName;

            return _currentStep + 1;
        }

        private async Task<int> FinalConfirmationAsync()
        {
            IPool chosenPool = _data.GetChosenPool();
            (IHasher cpuHasher, IHasher gpuHasher) = _data.GetChosenHasher();
            (Wallet wallet, string publicKey) = await _settings.GetWalletAsync();

            SelectionPrompt<int> selectionPrompt = new SelectionPrompt<int>();
            selectionPrompt.Title($"All settings can be manually changed in [b]{Settings.FilePath}[/]\n\nWallet: {publicKey ?? "??"}\nHasher: CPU - {cpuHasher?.Name ?? "N/A"} ({_settings.CPUThreads} threads), GPU - {gpuHasher?.Name ?? "N/A"}\nPool: {chosenPool?.DisplayName ?? "None"}\n");
            selectionPrompt.EnableSearch();
            selectionPrompt.AddChoice(0);
            selectionPrompt.AddChoice(1);
            selectionPrompt.AddChoice(2);

            selectionPrompt.UseConverter((i) =>
            {
                switch (i)
                {
                    case 0:
                        return "Confirm (save changes)";
                    case 1:
                        return "Restart Setup";
                    case 2:
                        return "Exit (don't save changes)";
                }

                return "??";
            });

            int result = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            switch (result)
            {
                case 0:
                    await _settings.SaveAsync();
                    return _currentStep + 1;
                case 1:
                    return 0;
                case 2:
                    await _settings.ReloadAsync();
                    return _currentStep + 1;
            }

            return 0;
        }

        private async Task CreateNewWallet(Wallet solanaWallet)
        {
            if(solanaWallet != null)
            {
                ConfirmationPrompt test = new ConfirmationPrompt($"Replace existing wallet ({solanaWallet.Account.PublicKey})?");
                test.DefaultValue = false;

                if(!await test.ShowAsync(AnsiConsole.Console, _cts.Token))
                {
                    AnsiConsole.Clear();

                    return;
                }

                AnsiConsole.Clear();
            }

            string executableDirectory = Utils.GetExecutableDirectory();

            Mnemonic mnemonic = new Mnemonic(WordList.English, WordCount.Twelve);
            SelectionPrompt<string> selectionPrompt = new SelectionPrompt<string>();
            Wallet wallet = new Wallet(mnemonic);

            selectionPrompt.Title($"Public Key: {wallet.Account.PublicKey}\n\nKey phrase: [green]{String.Join(", ", mnemonic.Words)}[/].\nA file named '[bold]id.json[/]' will also be created in [bold]{executableDirectory}[/] that the client will use\n[red]Highly recommended to save a copy of each[/]");
            selectionPrompt.AddChoices("Confirm", "Back (discard key)");

            string result = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            if(result == "Confirm")
            {
                string keyFile = Path.Combine(executableDirectory, "id.json");

                await File.WriteAllTextAsync(keyFile, JsonConvert.SerializeObject(wallet.Account.PrivateKey.KeyBytes.ToList()));
                _settings.KeyFile = keyFile;
                _settings.HasPrivateKey = true;
            }
        }

        private async Task SearchWallet()
        {
            List<(Wallet wallet, string path)> potentialWallets = new List<(Wallet wallet, string path)>();

            await AddWallet(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "solana", "id.json"));
            await AddWallet(Path.Combine(Utils.GetExecutableDirectory(), "id.json"));

            SelectionPrompt<int> selectionPrompt = new SelectionPrompt<int>();
            selectionPrompt.Title(potentialWallets.Count == 0 ? "Found no wallet keys in default location for solana-cli or client" : $"Found {potentialWallets.Count} potential wallets");
            selectionPrompt.EnableSearch();
            selectionPrompt.UseConverter((i) =>
            {
                switch (i)
                {
                    case -1:
                        return "Manual Search";
                    case -2:
                        return "Exit";
                    default:
                        if(i >= potentialWallets.Count)
                        {
                            return "???";
                        }

                        return $"Wallet: {potentialWallets[i].wallet.Account.PublicKey}. Path: {potentialWallets[i].path}";
                }
            });
            
            for(int i = 0; i < potentialWallets.Count; i++)
            {
                selectionPrompt.AddChoice(i);
            }

            selectionPrompt.AddChoice(-1);
            selectionPrompt.AddChoice(-2);

            int selection = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            if(selection >= 0)
            {
                _settings.KeyFile = potentialWallets[selection].path;
                _settings.HasPrivateKey = true;
            }
            else if(selection == -1)
            {
                while (true)
                {
                    TextPrompt<string> filePath = new TextPrompt<string>("Solana keypair file path:");
                    filePath.AllowEmpty();
                    string path = await filePath.ShowAsync(AnsiConsole.Console, _cts.Token);
                    AnsiConsole.Clear();

                    if(String.IsNullOrEmpty(path))
                    {
                        await SearchWallet();
                        return;
                    }


                    Wallet wallet = await AddWallet(path);

                    if (wallet == null)
                    {
                        string reason = File.Exists(path) ? "Failed to import key" : "File doesn't exist";

                        ConfirmationPrompt prompt = new ConfirmationPrompt($"{reason} ({path}). Try again?");

                        if(await prompt.ShowAsync(AnsiConsole.Console, _cts.Token))
                        {
                            continue;
                        }

                        AnsiConsole.Clear();

                        return;
                    }
                    else
                    {
                        _settings.KeyFile = path;
                        _settings.HasPrivateKey = true;

                        return;
                    }
                }
            }

            //Adds to a list and returns
            async Task<Wallet> AddWallet(string file)
            {
                if(!File.Exists(file))
                {
                    return null;
                }

                string text = await File.ReadAllTextAsync(file);

                try
                {
                    byte[] keyPair = JsonConvert.DeserializeObject<byte[]>(text);

                    if (keyPair == null)
                    {
                        return null;
                    }

                    Wallet wallet = new Wallet(keyPair, seedMode: SeedMode.Bip39);

                    potentialWallets.Add((wallet, file));

                    return wallet;
                }
                catch(Exception)
                {
                    //Might be good to log reason, but should only fail due to user changing the file

                    return null;
                }
            }
        }

        private async Task SetupPublicKey(string current)
        {
            Base58Encoder encoder = new Base58Encoder();

            TextPrompt<string> publicKeyPrompt = new TextPrompt<string>("Public Key:");
            publicKeyPrompt.Validate((str) =>
            {
                try
                {
                    if (encoder.DecodeData(str).Length != 32)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }

                return true;
            });

            publicKeyPrompt.DefaultValue(current);
            string publicKey = await publicKeyPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            AnsiConsole.Clear();

            if (String.IsNullOrEmpty(publicKey))
            {
                _settings.PublicKey = null;

                return;
            }

            _settings.HasPrivateKey = false;
            _settings.KeyFile = null;
            _settings.PublicKey = publicKey;
        }
    }
}
