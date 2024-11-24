using Blake2Sharp;
using Equix;
using Hardware.Info;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;
using Newtonsoft.Json;
using Org.BouncyCastle.Asn1.X509.Qualified;
using OrionClientLib.Hashers;
using OrionClientLib.Modules.Models;
using OrionClientLib.Modules.SettingsData;
using OrionClientLib.Pools;
using OrionClientLib.Utilities;
using Solnet.Wallet;
using Solnet.Wallet.Bip39;
using Solnet.Wallet.Utilities;
using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules
{
    public class SettingsModule : IModule
    {
        public string Name { get; } = "Configure Settings";

        private int _currentStep = 0;
        private List<Func<Task<int>>> _steps = new List<Func<Task<int>>>();
        private Data _data;
        private Settings _settings => _data?.Settings;
        private CancellationTokenSource _cts;
        private string _errorMessage = String.Empty;

        public SettingsModule()
        {
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
                Details mainMenu = new Details
                {
                    MenuName = "Main Menu"
                };

                List<Details> menu = GetSettingDetails(_settings, mainMenu);

                mainMenu.InnerSettings = menu;

                Details previousDetails = mainMenu;

                while(true)
                {
                    Details details = await DisplaySettings(previousDetails, menu, _cts.Token);
                    if(details.IsBack)
                    {
                        if (details.ParentMenu == null)
                        {
                            if (details.SaveChanges)
                            {
                                await _settings.SaveAsync();
                            }

                            break;
                        }

                        menu = details.ParentMenu?.InnerSettings;
                        previousDetails = details.ParentMenu;


                        continue;
                    }
                    else if (details.HasInnerSettings)
                    {
                        menu = details.InnerSettings;
                        previousDetails = details;

                        continue;
                    }

                    //Handle settings
                    await HandleSetting(details, _cts.Token);
                }
            }
            catch(TaskCanceledException)
            {
                reloadSettings = true;
            }

            if(reloadSettings)
            {
                await _settings.ReloadAsync();

                return (false, "Unsaved changes reverted");
            }

            return (true, String.Empty);
        }

        private async Task<bool> HandleSetting(Details details, CancellationToken token)
        {
            if(details.Type == typeof(bool))
            {
                bool newValue = await HandleBool($"Enable '{details.Attribute.Name}'", (bool)details.Property.GetValue(details.Obj));

                details.Property.SetValue(details.Obj, newValue);
            }
            else if (details.Type == typeof(int))
            {
                details.Property.SetValue(details.Obj, await HandleInt($"Set '{details.Attribute.Name}'", (int)details.Property.GetValue(details.Obj), details.Validator));
            }

            AnsiConsole.Clear();

            return false;

            async Task<bool> HandleBool(string message, bool defaultOption)
            {
                ConfirmationPrompt confirmationPrompt = new ConfirmationPrompt(message);
                confirmationPrompt.DefaultValue = defaultOption;

                return await confirmationPrompt.ShowAsync(AnsiConsole.Console, token);
            }

            async Task<int> HandleInt(string message, int defaultOption, SettingValidatorAttribute validation)
            {
                if(validation is OptionSettingValidation<int> optionValidation)
                {
                    SelectionPrompt<int> selectionPrompt = new SelectionPrompt<int>();
                    selectionPrompt.Title(message);
                    selectionPrompt.AddChoices(optionValidation.Options.OrderByDescending(x => x == defaultOption));
                    selectionPrompt.UseConverter((v) =>
                    {
                        if(v == defaultOption)
                        {
                            return $"{v} [[Current]]";
                        }

                        return v.ToString();
                    });
                    return await selectionPrompt.ShowAsync(AnsiConsole.Console, token);
                }

                TextPrompt<int> textPrompt = new TextPrompt<int>(message);
                textPrompt.DefaultValue(defaultOption);
                textPrompt.Validate((i) =>
                {
                    if(validation == null)
                    {
                        return true;
                    }

                    return validation.Validate(i);
                });

                return await textPrompt.ShowAsync(AnsiConsole.Console, token);
            }
        }

        private async Task<Details> DisplaySettings(Details previousDetails, List<Details> details, CancellationToken token)
        {
            Details backChoice = new Details
            {
                IsBack = true,
                ParentMenu = previousDetails.ParentMenu
            };

            Details saveChanges = new Details
            {
                IsBack = true,
                SaveChanges = true,
                ParentMenu = previousDetails.ParentMenu
            };

            var allChanges = await _settings.GetChanges();

            SelectionPrompt<Details> selectionPrompt = new SelectionPrompt<Details>();

            StringBuilder builder = new StringBuilder();

            if(previousDetails?.Attribute?.Description == null)
            {
                builder.Append($"Configure settings");

                if (allChanges.Count > 0)
                {
                    builder.Append(" - [aqua]Ctrl + C to exit without saving changes[/]");
                }

                builder.AppendLine();
            }
            else
            {
                builder.AppendLine($"{previousDetails.Attribute.Description}");
            }
            builder.Append($"   [teal]Path: ");

            string str = previousDetails.MenuName;

            while(previousDetails.ParentMenu != null)
            {
                if(String.IsNullOrEmpty(str))
                {
                    str = $"{previousDetails.ParentMenu.MenuName}";
                }
                else
                {
                    str = $"{previousDetails.ParentMenu.MenuName} > {str}";
                }
                previousDetails = previousDetails.ParentMenu;
            }

            builder.Append(str);
            builder.AppendLine("[/]");

            selectionPrompt.Title(builder.ToString());
            selectionPrompt.EnableSearch();
            selectionPrompt.UseConverter((det) =>
            {
                if(det.IsBack)
                {
                    if (allChanges.Count > 0)
                    {
                        if (det.SaveChanges)
                        {
                            StringBuilder builder = new StringBuilder();
                            builder.AppendLine($"[aqua]-- Save Changes & Exit --[/]");

                            int counter = 0;

                            foreach(var change in allChanges)
                            {
                                builder.AppendLine($"         [aqua]{++counter}. {change.Path} | [red]{change.OldValue}[/] -> [green]{change.NewValue}[/][/]");
                            }

                            return builder.ToString();
                        }
                        else
                        {
                            return $"[aqua]<-- Back[/]";
                        }
                    }
                    else if (det.ParentMenu == null)
                    {
                        return $"[aqua]-- Exit --[/]";
                    }
                    else
                    {
                        return $"[aqua]<-- Back[/]";
                    }
                }

                if(det.HasInnerSettings)
                {
                    return $"[green]{det.Attribute.Name}[/]";
                }

                return $"{det.Attribute.Name} ({det.Property.GetValue(det.Obj)}) - {det.Attribute.Description}";
            });

            selectionPrompt.AddChoices(details);

            if (allChanges.Count > 0 && backChoice.ParentMenu == null)
            {
                selectionPrompt.AddChoice(saveChanges);
            }
            else
            {
                selectionPrompt.AddChoice(backChoice);
            }

            return await selectionPrompt.ShowAsync(AnsiConsole.Console, token);
        }

        private List<Details> GetSettingDetails(object obj, Details upperMenu = null)
        {
            List<Details> details = new List<Details>();

            foreach (var property in obj.GetType().GetProperties())
            {
                SettingDetailsAttribute attr = property.GetCustomAttribute<SettingDetailsAttribute>();

                if(attr == null)
                {
                    continue;
                }

                Details newDetails = new Details
                {
                    Attribute = attr,
                    Property = property,
                    ParentMenu = upperMenu,
                    Validator = property.GetCustomAttribute<SettingValidatorAttribute>(),
                    Obj = obj
                };

                if(property.PropertyType != typeof(string) && property.PropertyType.IsClass)
                {
                    newDetails.MenuName = attr.Name;
                    newDetails.InnerSettings = GetSettingDetails(property.GetValue(obj), newDetails);
                }

                details.Add(newDetails);
            }

            return details;
        }

        private class Details
        { 
            public string MenuName { get; set; }
            public Details ParentMenu { get; set; }
            public bool IsBack { get; set; }
            public bool SaveChanges { get; set; }
            public SettingDetailsAttribute Attribute { get; set; }
            public SettingValidatorAttribute Validator { get; set; }
            public object Obj { get; set; }
            public Type Type => Property?.PropertyType;
            public PropertyInfo Property { get; set; }
            public List<Details> InnerSettings { get; set; }
            public bool HasInnerSettings => InnerSettings != null && InnerSettings.Count > 0;
        }

        
        private async Task<int> WalletSetupAsync()
        {
            while (true)
            {
                (Wallet solanaWallet, string publicKey) = await _settings.GetWalletAsync();

                SelectionPrompt<string> selectionPrompt = new SelectionPrompt<string>();
                selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nSetup solana wallet. Current: {publicKey ?? "??"}. Path: {(_settings.HasPrivateKey ? (_settings.KeyFile ?? "N/A") : "N/A")}");

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
            (IHasher cpuHasher, IHasher gpuHasher) = _data.GetChosenHasher();

            SelectionPrompt<IHasher> selectionPrompt = new SelectionPrompt<IHasher>();
            selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nSelect CPU hashing implementation. Run benchmark to see hashrates");
            selectionPrompt.UseConverter((hasher) =>
            {
                if (hasher == null)
                {
                    return "[aqua]<-- Previous Step[/]";
                }

                string chosenText = String.Empty;

                if (hasher == cpuHasher)
                {
                    chosenText = "[b][[Current]][/] ";
                }

                return $"{chosenText}{hasher.Name} - {hasher.Description}";
            });

            selectionPrompt.AddChoices(_data.Hashers.Where(x => x.HardwareType == IHasher.Hardware.CPU).OrderByDescending(x =>x == cpuHasher));
            selectionPrompt.AddChoice(null);

            cpuHasher = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            if(cpuHasher == null)
            {
                return _currentStep - 1;
            }

            _settings.CPUHasher = cpuHasher.Name;

            return await ThreadCountAsync();
        }

        //Reduce to single method later
        private async Task<int> ChooseGPUHasherAsync()
        {
            (IHasher cpuHasher, IHasher gpuHasher) = _data.GetChosenHasher();

            SelectionPrompt<IHasher> selectionPrompt = new SelectionPrompt<IHasher>();
            selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nSelect GPU hashing implementation. Run benchmark to see hashrates");
            selectionPrompt.UseConverter((hasher) =>
            {
                if(hasher == null)
                {
                    return "[aqua]<-- Previous Step[/]";
                }

                string chosenText = String.Empty;

                if (hasher == gpuHasher)
                {
                    chosenText = "[b][[Current]][/] ";
                }

                return $"{chosenText}{hasher.Name} - {hasher.Description}";
            });

            selectionPrompt.AddChoices(_data.Hashers.Where(x => x.HardwareType == IHasher.Hardware.GPU).OrderByDescending(x => x == gpuHasher));
            selectionPrompt.AddChoice(null);

            gpuHasher = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            if(gpuHasher == null)
            {
                return _currentStep - 1;
            }

            _settings.GPUHasher = gpuHasher.Name;

            if(gpuHasher is DisabledHasher)
            {
                _settings.GPUHasher = gpuHasher.Name;

                return _currentStep + 1;
            }

            IGPUHasher hasher = (IGPUHasher)gpuHasher;
            List<Device> devices = hasher.GetDevices(false);
            HashSet<Device> validDevices = new HashSet<Device>(hasher.GetDevices(true));

            //Allow device selection
            MultiSelectionPrompt<Device> deviceSelectionPrompt = new MultiSelectionPrompt<Device>();
            deviceSelectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nSelect GPUs to use. Selecting different GPU types may cause performance issues");
            deviceSelectionPrompt.UseConverter((device) =>
            {
                bool selected = _settings.GPUDevices.Contains(devices.IndexOf(device));

                return $"{(selected ? "[b][[Current]][/] " : String.Empty)}{device.Name} - {device.AcceleratorType}{(!validDevices.Contains(device) ? " [red][[Not supported]][/]" : String.Empty)}";
            });


            //Shouldn't happen, but disable GPU hasher for now
            if (devices == null)
            {
                _settings.GPUHasher = "Disabled";

                return _currentStep + 1;
            }

            var groups = devices.OrderByDescending(x => x.NumMultiprocessors).GroupBy(x => x.Name);

            foreach(var group in groups)
            {
                if (group.Count() == 1)
                {
                    deviceSelectionPrompt.AddChoice(group.First());
                }
                else
                {
                    deviceSelectionPrompt.AddChoiceGroup(group.First(), group);
                }
            }

            List<Device> result = await deviceSelectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

            List<int> chosenGPUs = new List<int>();

            foreach (Device device in result)
            {
                chosenGPUs.Add(devices.IndexOf(device));
            }

            _settings.GPUDevices = chosenGPUs;

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

                if (!choices.Any(x => x.Item1 == _settings.CPUSetting.CPUThreads))
                {
                    choices.Add((_settings.CPUSetting.CPUThreads, "[[Current]]"));
                }
            }

            choices.Add((-1, "[aqua]<-- Previous Step[/]"));

            SelectionPrompt<(int, string)> selectionPrompt = new SelectionPrompt<(int, string)>();
            selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nSelect total threads. Highest value recommended. Current: {_settings.CPUSetting.CPUThreads}");
            selectionPrompt.UseConverter((tuple) =>
            {
                if(tuple.Item1 > 0)
                {
                    return $"{tuple.Item1} {tuple.Item2}";
                }

                return tuple.Item2;
            });

            selectionPrompt.AddChoices(choices.OrderByDescending(x => x.Item1 == _settings.CPUSetting.CPUThreads).ThenByDescending(x => x.Item1));

            (int, string) choice = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);
            
            if(choice.Item1 == -1)
            {
                //We're going back to the start of this step
                return _currentStep;
            }
            else if(choice.Item1 == 0)
            {
                while (true)
                {
                    TextPrompt<int> textPrompt = new TextPrompt<int>($"Total threads (min: 1, max: {totalThreads}):");
                    textPrompt.DefaultValue(_settings.CPUSetting.CPUThreads);

                    int result = await textPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

                    AnsiConsole.Clear();

                    if (result > 0 && result <= totalThreads)
                    {
                        _settings.CPUSetting.CPUThreads = result;
                        break;
                    }
                }
            }
            else
            {
                _settings.CPUSetting.CPUThreads = choice.Item1;
            }

            return _currentStep + 1;
        }

        private async Task<int> ChoosePoolAsync()
        {
            IPool chosenPool = _data.GetChosenPool();
            
            SelectionPrompt<IPool> selectionPrompt = new SelectionPrompt<IPool>();
            selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nPool Selection{(!String.IsNullOrEmpty(_errorMessage) ? $"\n[red]Error: {_errorMessage}[/]\n" : String.Empty)}");
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
            selectionPrompt.Title($"Step: {_currentStep + 1}/{_steps.Count}\n\nAll settings can be manually changed in [b]{Settings.FilePath}[/]\n\nWallet: {publicKey ?? "??"}\nHasher: CPU - {cpuHasher?.Name ?? "N/A"} ({_settings.CPUSetting.CPUThreads} threads), GPU - {gpuHasher?.Name ?? "N/A"}\nPool: {chosenPool?.DisplayName ?? "None"}\n");
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
                _settings.PublicKey = String.Empty;

                return;
            }

            _settings.HasPrivateKey = false;
            _settings.KeyFile = String.Empty;
            _settings.PublicKey = publicKey;
        }
    }
}
