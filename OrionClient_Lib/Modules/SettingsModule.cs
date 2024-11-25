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
            if(details.Validator is TypeValidator validator)
            {
                string oldValue = details.Property.GetValue(details.Obj)?.ToString() ?? String.Empty;
                string newValue = await HandleType($"Choose '{details.Attribute.Name}'", oldValue, validator.Options.ToList());

                details.Property.SetValue(details.Obj, newValue);
            }
            else if(details.Type == typeof(bool))
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

            async Task<string> HandleType(string message, string defaultOption, List<ISettingInfo> options)
            {
                SelectionPrompt<ISettingInfo> selectionPrompt = new SelectionPrompt<ISettingInfo>();
                selectionPrompt.Title(message);
                selectionPrompt.UseConverter((opt) =>
                {
                    if(opt == null)
                    {
                        return "Disabled";
                    }

                    if(opt.Name == defaultOption)
                    {
                        return $"[[Current]] {opt.Name} - {opt.Description}";
                    }

                    return $"{opt.Name} - {opt.Description}";
                });

                //Special case for hashers as the disabled ones don't inheret a base class
                if (options.FirstOrDefault() is IHasher tHasher)
                {
                    options.Add(tHasher.HardwareType == IHasher.Hardware.CPU ? new DisabledCPUHasher() : new DisabledGPUHasher());
                }

                foreach (ISettingInfo option in options.OrderByDescending(x => x.Name == defaultOption))
                {
                    if(option is IHasher hasher)
                    {
                        if(!hasher.IsSupported())
                        {
                            continue;
                        }
                    }

                    selectionPrompt.AddChoice(option);
                }


                    var result = await selectionPrompt.ShowAsync(AnsiConsole.Console, token);

                return result?.Name ?? "Disabled";
            }

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

        #region Might use later

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

        #endregion
    }
}
