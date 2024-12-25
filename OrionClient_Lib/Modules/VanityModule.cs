using Blake2Sharp;
using Chaos.NaCl;
using Equix;
using ILGPU.Runtime;
using Newtonsoft.Json;
using NLog;
using OrionClientLib.Hashers;
using OrionClientLib.Hashers.GPU.RTX4090Opt;
using OrionClientLib.Modules.Models;
using OrionClientLib.Modules.Vanity;
using OrionClientLib.Pools;
using OrionClientLib.Pools.Models;
using OrionClientLib.Utilities;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using Spectre.Console;
using Spectre.Console.Prompts;
using Spectre.Console.Rendering;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules
{
    public class VanityModule : IModule
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public string Name { get; } = "Vanity Finder";

        private Data _currentData;
        private CancellationTokenSource _cts;
        private bool _stop = false;
        private Layout _uiLayout = null;
        private Table _hashrateTable = null;
        private Table _vanityTable = null;
        private VanityGPU _vanity;
        private byte[] _lengthToRow;

        public async Task<ExecuteResult> ExecuteAsync(Data data)
        {
            _currentData = data;

            return new ExecuteResult { Exited = _stop, Renderer = _uiLayout };
        }

        public async Task ExitAsync()
        {
            _logger.Log(LogLevel.Debug, $"Exiting out of vanity module...");

            await _vanity.StopAsync();
            _vanity.OnHashrateUpdate -= _vanity_OnHashrateUpdate;

            _cts.Cancel();
            _stop = true;
        }

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(VanityKernel))]
        public async Task<(bool, string)> InitializeAsync(Data data)
        {
            _cts = new CancellationTokenSource();
            _stop = false;
            _currentData = data;
            _vanity ??= new VanityGPU();
            _vanity.OnHashrateUpdate += _vanity_OnHashrateUpdate;
            string error = String.Empty;

            if (!_vanity.InitializedVanities)
            {
                AnsiConsole.WriteLine("Loading vanity information ...");
                await _vanity.InitializeVanities(data.Settings);
                AnsiConsole.Clear();
            }

            GenerateUI();

            while (true)
            {
                //Exiting
                if (!await DisplayMenu(error))
                {
                    _stop = true;

                    return (true, String.Empty);
                }

                error = String.Empty;
                AnsiConsole.Clear();

                var result = await _vanity.InitializeAsync(data.Settings);

                if (!result.success)
                {
                    error = result.message;

                    continue;
                }


                return (true, String.Empty);
            }
        }

        private async Task<bool> DisplayMenu(string lastError = null)
        {
            while (true)
            {
                SelectionPrompt<string> selectionPrompt = new SelectionPrompt<string>();
                selectionPrompt.Title($"Vanity Finder. Purpose is for interesting burner wallets." +
                    $"\n\nVanities To Find: {_vanity.SearchVanities} (unique)" +
                    $"{(_vanity.SearchVanities == 0 ? $" [aqua]Add vanities to find to '{Path.Combine(Settings.VanitySettings.Directory, _currentData.Settings.VanitySetting.VanitySearchFile)}' then reload[/]" : String.Empty)}" +
                    $"{(_vanity.InvalidLines ? $" [red]{_vanity.InvalidMessage}[/]" : "")}" +
                    $"\nTotal Vanities Found: {_vanity.FoundWallets} (nonunique) / {_vanity.FoundUniqueWallets} (unique)" +
                    $"\n\nValidate public keys are accurate prior to sending any funds{(!String.IsNullOrEmpty(lastError) ? $"\n\n[red]{lastError}[/]" : String.Empty)}");

                const string run = "Run";
                const string view = "View Wallets";
                const string setup = "Setup";
                const string reload = "Reload Vanity File";
                const string exit = "Exit";

                if(_currentData.Settings.VanitySetting.GPUDevices?.Count > 0)
                {
                    selectionPrompt.AddChoice(run);
                }

                selectionPrompt.AddChoices(setup, view, reload, exit);

                string choice = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

                try
                {
                    switch (choice)
                    {
                        case run:
                            return true;
                        case view:
                            await ViewWallets();
                            break;
                        case setup:
                            await SelectGPUs();
                            break;
                        case reload:
                            AnsiConsole.Clear();
                            AnsiConsole.WriteLine("Reloading vanity information ...");
                            await _vanity.InitializeVanities(_currentData.Settings);
                            GenerateVanityFoundUI();
                            AnsiConsole.Clear();
                            break;
                        case exit:
                            return false;
                    }
                }
                catch(PromptAbortException)
                {
                    //Returns to main menu
                    AnsiConsole.Clear();
                }
            }
        }

        private async Task SelectGPUs()
        {
            List<Device> devices = _vanity.GetDevices(false);
            HashSet<Device> validDevices = new HashSet<Device>(_vanity.GetDevices(true));
            var vanitySettings = _currentData.Settings.VanitySetting;

            MultiSelectionPrompt<Device> deviceSelectionPrompt = new MultiSelectionPrompt<Device>();
            deviceSelectionPrompt.Title($"Select GPUs to use. Selecting different GPU types may cause performance issues.\n[gray]Press `ESC` to return to vanity menu[/]");
            deviceSelectionPrompt.AbortKey = ConsoleKey.Escape;

            deviceSelectionPrompt.UseConverter((device) =>
            {
                bool selected = vanitySettings.GPUDevices?.Contains(devices.IndexOf(device)) == true;

                return $"{(selected ? "[b][[Current]][/] " : String.Empty)}{device.Name} - {device.AcceleratorType}{(!validDevices.Contains(device) ? " [red][[Not supported]][/]" : String.Empty)}";
            });


            var groups = devices.OrderByDescending(x => x.NumMultiprocessors).GroupBy(x => x.Name);

            foreach (var group in groups)
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

            vanitySettings.GPUDevices = chosenGPUs;

            await _currentData.Settings.SaveAsync();
            GenerateUI();
        }

        private async Task ViewWallets()
        {
            SelectionPrompt<VanityTracker> selectionPrompt = new SelectionPrompt<VanityTracker>();
            selectionPrompt.Title($"View Vanities By Length\n[gray]Press `ESC` to return to vanity menu[/]");
            selectionPrompt.AbortKey = ConsoleKey.Escape;

            selectionPrompt.UseConverter((tracker) =>
            {
                if (tracker.VanityLength == 0)
                {
                    return $"View All - {_vanity.FoundWallets} Found";
                }

                return $"{tracker.VanityLength} - {tracker.Total} Found";
            });

            selectionPrompt.AddChoice(new VanityTracker { VanityLength = 0 });

            foreach (var kvp in _vanity.VanitiesByLength.OrderByDescending(x => x.Key))
            {
                if (kvp.Value.Total > 0)
                {
                    selectionPrompt.AddChoice(kvp.Value);
                }
            }

            VanityTracker wallets = await selectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);
            List<FoundVanity> vanities = wallets.Vanities;

            if (wallets.VanityLength == 0)
            {
                vanities = new List<FoundVanity>();

                foreach (var kvp in _vanity.VanitiesByLength)
                {
                    vanities.AddRange(kvp.Value.Vanities);
                }
            }

            while (true)
            {
                SelectionPrompt<FoundVanity> vanitySelectionPrompt = new SelectionPrompt<FoundVanity>();
                vanitySelectionPrompt.Title($"Select vanity to view private key.\n[gray]Press `ESC` to return to vanity menu[/]");
                vanitySelectionPrompt.EnableSearch();
                vanitySelectionPrompt.AbortKey = ConsoleKey.Escape;
                vanitySelectionPrompt.PageSize = 20;
                vanitySelectionPrompt.OnlyShowSearchedResults = true;
                vanitySelectionPrompt.SearchFunc = (FoundVanity vanity, string search) => { 
                    return vanity.VanityText.StartsWith(search, StringComparison.OrdinalIgnoreCase); 
                };

                vanitySelectionPrompt.UseConverter((vanity) =>
                {
                    if(vanity.Exported)
                    {
                        return $"[green]{vanity.VanityText} - {vanity.PublicKey}[/]";
                    }

                    return $"[red]{vanity.VanityText} - {vanity.PublicKey}[/]";
                });

                vanitySelectionPrompt.AddChoices(vanities.OrderBy(x => x.PublicKey));

                FoundVanity vanity = await vanitySelectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

                SelectionPrompt<string> optionSelectionPrompt = new SelectionPrompt<string>();

                Base58Encoder encoder = new Base58Encoder();

                byte[] privateKey = encoder.DecodeData(vanity.PrivateKey);
                byte[] publicKey = encoder.DecodeData(vanity.PublicKey);

                List<byte> fullKey = [.. privateKey, .. publicKey];

                if (!ValidateVanity(vanity))
                {
                    optionSelectionPrompt.Title($"[red]Vanity {vanity.PublicKey} is invalid. Private key can't produce correct public key[/]");
                }
                else
                {
                    optionSelectionPrompt.Title($"Exporting will create a keypair file that can be 'searched' in the main setup\n\nWallet: {vanity.PublicKey}\nWallet Import (Solflare/Phantom): {encoder.EncodeData(fullKey.ToArray())}");
                }

                const string export = "Export";
                const string returnToList = "Return To List";
                const string exit = "Exit to Vanity Menu";

                if (!IsExported(vanity.PublicKey))
                {
                    optionSelectionPrompt.AddChoice(export);
                }

                optionSelectionPrompt.AddChoice(returnToList);
                optionSelectionPrompt.AddChoice(exit);

                string choice = await optionSelectionPrompt.ShowAsync(AnsiConsole.Console, _cts.Token);

                switch (choice)
                {
                    case export:
                        await ExportWallet(vanity.PublicKey, fullKey);
                        vanity.Exported = true;
                        continue;
                    case returnToList:
                        continue;
                    case exit:
                        break;
                }

                break;
            }
        }

        private bool ValidateVanity(FoundVanity vanity)
        {
            Base58Encoder encoder = new Base58Encoder();

            byte[] privateKey = encoder.DecodeData(vanity.PrivateKey);
            byte[] publicKey = encoder.DecodeData(vanity.PublicKey);

            byte[] expectedPublicKey = Ed25519.ExpandedPrivateKeyFromSeed(privateKey)[32..64];

            //Accurate public key
            if (expectedPublicKey.SequenceEqual(publicKey))
            {
                //Valid base58
                string vanityStr = encoder.EncodeData(expectedPublicKey);

                return vanity.PublicKey == vanityStr;
            }

            return false;
        }

        private bool IsExported(string vanity)
        {
            string directory = Path.Combine(Utils.GetExecutableDirectory(), Settings.VanitySettings.Directory, "wallets");

            Directory.CreateDirectory(directory);

            return File.Exists(Path.Combine(directory, $"{vanity}.json"));
        }

        private async Task ExportWallet(string vanity, List<byte> key)
        {
            string directory = Path.Combine(Utils.GetExecutableDirectory(), Settings.VanitySettings.Directory, "wallets");

            Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(Path.Combine(directory, $"{vanity}.json"), JsonConvert.SerializeObject(key));
        }

        private void _vanity_OnHashrateUpdate(object? sender, Vanity.VanityHashingInfo e)
        {
            int index = e.Index;

            _hashrateTable.UpdateCell(index, 2, e.SessionSpeed.ToString());
            _hashrateTable.UpdateCell(index, 3, e.Speed.ToString());
            _hashrateTable.UpdateCell(index, 4, $"{e.ExecutionTime.TotalMilliseconds:0.00}ms");

            if (e.ExecutionTime.TotalMilliseconds < e.VanitySearchTime.TotalMilliseconds)
            {
                _hashrateTable.UpdateCell(index, 5, $"[red]{e.VanitySearchTime.TotalMilliseconds:0.00}ms[/]");
            }
            else
            {
                _hashrateTable.UpdateCell(index, 5, $"{e.VanitySearchTime.TotalMilliseconds:0.00}ms");
            }

            _hashrateTable.UpdateCell(index, 6, $"{PrettyFormatTime(e.Runtime)}");

            //Update all rows for now
            foreach (var kvp in _vanity.VanitiesByLength.OrderByDescending(x => x.Key))
            {
                int row = _lengthToRow[kvp.Key];

                _vanityTable.UpdateCell(row, 1, $"{kvp.Value.UniqueCount}{(kvp.Value.SessionUniqueCount > 0 ? $" [green](+{kvp.Value.SessionUniqueCount})[/]" : String.Empty)}");
                _vanityTable.UpdateCell(row, 2, $"{kvp.Value.Total}{(kvp.Value.SessionCount > 0 ? $" [green](+{kvp.Value.SessionCount})[/]" : String.Empty)}");
                _vanityTable.UpdateCell(row, 3, kvp.Value.Searching.ToString());
            }

            string PrettyFormatTime(TimeSpan span)
            {
                string formatted = String.Format("{0}{1}{2}{3}",
                    span.Duration().Days > 0 ? String.Format("{0:0}d ", span.Days) : String.Empty,
                    span.Duration().Hours > 0 ? String.Format("{0:0}h ", span.Hours) : String.Empty,
                    span.Duration().Minutes > 0 ? String.Format("{0:0}m ", span.Minutes) : String.Empty,
                    span.Duration().Seconds > 0 ? String.Format("{0:0}s", span.Seconds) : String.Empty);

                if (formatted.EndsWith(", "))
                {
                    formatted = formatted.Substring(0, formatted.Length - 2);
                }

                if (String.IsNullOrEmpty(formatted))
                {
                    formatted = "0s";
                }

                return formatted;
            }
        }

        private void GenerateUI()
        {
            //Generate UI
            _uiLayout = new Layout("minerModule").SplitColumns(
                new Layout("hashrate"),
                new Layout("vanityInfo")
                );
            //_uiLayout["hashrate"].Ratio = 85;
            //_uiLayout["vanityInfo"].Ratio = 100;
            
            _hashrateTable = new Table();
            
            _hashrateTable.AddColumn(new TableColumn("Name").Centered());
            _hashrateTable.AddColumn(new TableColumn("Threads").Centered());
            _hashrateTable.AddColumn(new TableColumn("Session Hashrate").Centered());
            _hashrateTable.AddColumn(new TableColumn("Hashrate").Centered());
            _hashrateTable.AddColumn(new TableColumn("Execution Time").Centered());
            _hashrateTable.AddColumn(new TableColumn("Vanity Time").Centered());
            _hashrateTable.AddColumn(new TableColumn("Run Time").Centered());
            _hashrateTable.ShowRowSeparators = true;

            _vanityTable = new Table();
            //_vanityTable.Title = new TableTitle("Vanity Info");
            _vanityTable.AddColumns("Characters", "Unique", "Total", "Searching");
            _vanityTable.ShowRowSeparators = true;

            GenerateVanityFoundUI();

            _uiLayout["hashrate"].Update(_hashrateTable);
            _uiLayout["vanityInfo"].Update(_vanityTable);


            _uiLayout["hashrate"].Ratio = 100;
            _uiLayout["vanityInfo"].Ratio = 65;

            List<Device> devicesToUse = new List<Device>();
            List<Device> devices = _vanity.GetDevices(false); //All devices
            HashSet<Device> supportedDevices = new HashSet<Device>(_vanity.GetDevices(true)); //Only supported

            if (_currentData.Settings.VanitySetting.GPUDevices?.Count > 0)
            {
                foreach (var d in _currentData.Settings.VanitySetting.GPUDevices)
                {
                    if (d >= 0 && d < devices.Count)
                    {
                        if (supportedDevices.Contains(devices[d]))
                        {
                            devicesToUse.Add(devices[d]);
                        }
                    }
                }

                foreach (var device in devicesToUse)
                {
                    string gpuName = device.Name.Replace("NVIDIA ", "").Replace("GeForce ", "");

                    _hashrateTable.AddRow(gpuName, (_currentData.Settings.VanitySetting.VanityThreads == 0 ? Environment.ProcessorCount : _currentData.Settings.VanitySetting.VanityThreads).ToString(), "-", "-", "-", "-");
                }
            }
        }

        private void GenerateVanityFoundUI()
        {
            _vanityTable.Rows.Clear();
            _lengthToRow = null;

            byte row = 0;

            foreach (var kvp in _vanity.VanitiesByLength.OrderByDescending(x => x.Key))
            {
                _lengthToRow ??= new byte[kvp.Key + 1];
                _lengthToRow[kvp.Key] = row++;
                _vanityTable.AddRow(kvp.Key.ToString(), kvp.Value.UniqueCount.ToString(), kvp.Value.Total.ToString(), kvp.Value.Searching.ToString());
            }
        }
    }
}
