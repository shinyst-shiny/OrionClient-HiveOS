using Newtonsoft.Json;
using OrionClientLib.Hashers;
using OrionClientLib.Modules.Models;
using OrionClientLib.Modules.SettingsData;
using OrionClientLib.Pools;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib
{
    public class Settings
    {
        public static string FilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

        [SettingDetails("Pool", "Change pool implementation")]
        [TypeValidator<BasePool>()]
        public string Pool { get; set; }

        [Obsolete]
        public string CPUHasher { get; set; } = "Stock";

        [Obsolete]
        public string GPUHasher { get; set; } = "Disabled";

        public List<int> GPUDevices { get; set; } = new List<int>();

        public bool HasPrivateKey { get; set; }
        public string KeyFile { get; set; }
        public string PublicKey { get; set; }

        [Obsolete]
        public int CPUThreads { get; set; } = 1;
        [Obsolete]
        public bool AutoSetCPUAffinity { get; set; } = true;

        [Obsolete]
        public int MaxGPUBlockSize { get; set; } = 2048;
        [Obsolete]
        public int ProgramGenerationThreads { get; set; } = 0;

        //public bool EnableDebugging { get; set; }
        public bool MigratedSettings { get; set; } = false;

        [JsonIgnore]
        public bool NeedsSetup => String.IsNullOrEmpty(CPUHasher) || String.IsNullOrEmpty(GPUHasher) || String.IsNullOrEmpty(Pool) || (String.IsNullOrEmpty(PublicKey) && String.IsNullOrEmpty(KeyFile));


        [SettingDetails("View CPU Settings", "Configure CPU settings")]
        public CPUSettings CPUSetting { get; set; } = new CPUSettings();
        [SettingDetails("View GPU Settings", "Configure GPU settings")]
        public GPUSettings GPUSetting { get; set; } = new GPUSettings();

        public class CPUSettings
        {
            [SettingDetails("Hasher", "Change CPU hasher implementation")]
            [TypeValidator<BaseCPUHasher>()]
            public string CPUHasher { get; set; } = "Stock";

            [SettingDetails("Threads", "Total threads to use for mining (0 = all threads)")]
            [ThreadValidator]
            public int CPUThreads { get; set; } = 1;

            [SettingDetails("Auto Set CPU Affinity", "Automatically sets CPU affinity when only CPU mining is enabled and only using physical thread count (windows only)")]
            public bool AutoSetCPUAffinity { get; set; } = true;
        }

        public class GPUSettings
        {
            [SettingDetails("Hasher", "Change GPU hasher implementation")]
            [TypeValidator<BaseGPUHasher>()]
            public string GPUHasher { get; set; } = "Disabled";

            [SettingDetails("Batch Size", "Higher values use more ram and take longer to run. Lower values can cause lower hashrates")]
            [OptionSettingValidation<int>(2048, 1024, 512, 256, 128)]
            public int MaxGPUNoncePerBatch { get; set; } = 2048;

            [SettingDetails("Block Size", "Can try different values to see if HashX performance changes. GPU specific implementations will override this value.")]
            [OptionSettingValidation<int>(512, 256, 128, 64, 32, 16)]
            public int GPUBlockSize { get; set; } = 512;

            [SettingDetails("Program Generation Threads", "Total CPU threads to use to generation program instructions (0 = all threads)")]
            [ThreadValidator]
            public int ProgramGenerationThreads { get; set; } = 0;
        }

        public static async Task<Settings> LoadAsync()
        {
            if (!File.Exists(FilePath))
            {
                Settings settings = new Settings();
                settings.CPUSetting.CPUThreads = Environment.ProcessorCount;

                return settings;
            }

            var s = await File.ReadAllTextAsync(FilePath);
            var t = JsonConvert.DeserializeObject<Settings>(s);

            if (t == null)
            {
                throw new Exception("Failed to read setting file");
            }

            //Migrats to new settings
            if (!t.MigratedSettings)
            {
                t.CPUSetting.CPUThreads = t.CPUSetting.CPUThreads;
#pragma warning disable CS0612 // Type or member is obsolete
                t.CPUSetting.AutoSetCPUAffinity = t.AutoSetCPUAffinity;
                t.CPUSetting.CPUHasher = t.CPUHasher;
                t.GPUSetting.GPUHasher = t.GPUHasher;
                t.GPUSetting.MaxGPUNoncePerBatch = t.MaxGPUBlockSize;
                t.GPUSetting.ProgramGenerationThreads = t.ProgramGenerationThreads;
#pragma warning restore CS0612 // Type or member is obsolete
                t.MigratedSettings = true;
            }

            return t;
        }

        public async Task ReloadAsync()
        {
            Settings oldSettings = await LoadAsync();

            PropertyInfo[] properties = typeof(Settings).GetProperties();

            foreach(PropertyInfo property in properties)
            {
                if(property.SetMethod != null)
                {
                    var oldValue = property.GetValue(oldSettings);
                    property.SetValue(this, oldValue);
                }
            }
        }

        public async Task<List<SettingChange>> GetChanges()
        {
            List<SettingChange> changes = new List<SettingChange>();

            Settings oldSettings = await LoadAsync();

            CheckChanges(this, oldSettings);

            return changes;

            void CheckChanges(object baseObj, object oldSettings, string path = null)
            {
                foreach (PropertyInfo property in baseObj.GetType().GetProperties())
                {
                    SettingDetailsAttribute details = property.GetCustomAttribute<SettingDetailsAttribute>();

                    if (details != null)
                    {
                        var oldValue = property.GetValue(oldSettings);
                        var newValue = property.GetValue(baseObj);

                        if (property.PropertyType != typeof(string) && property.PropertyType.IsClass)
                        {
                            CheckChanges(newValue, oldValue, path == null ? details.Name : $"{path} > {details.Name}");
                        }
                        else if ((oldValue == null && newValue != null) || oldValue?.Equals(newValue) == false)
                        {
                            changes.Add(new SettingChange
                            {
                                OldValue = oldValue ?? "Unknown",
                                NewValue = newValue ?? "Unknown",
                                Path = path == null ? details.Name : $"{path} > {details.Name}",
                                Setting = details.Name
                            });
                        }
                    }
                }
            }
        }

        public Task SaveAsync()
        {
            return File.WriteAllTextAsync(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public async Task<(Wallet, string)> GetWalletAsync()
        {
            if(!HasPrivateKey)
            {
                if(String.IsNullOrEmpty(PublicKey))
                {
                    return default;
                }

                Base58Encoder encoder = new Base58Encoder();

                try
                {
                    byte[] key = encoder.DecodeData(PublicKey);

                    //Invalid length
                    if(key.Length != 32)
                    {
                        return default;
                    }
                }
                catch(Exception)
                {
                    return default;
                }

                return (null, PublicKey);
            }

            if(String.IsNullOrEmpty(KeyFile))
            {
                return default;
            }

            if(!File.Exists(KeyFile))
            {
                return default;
            }

            string text = await File.ReadAllTextAsync(KeyFile);
            byte[] keyPair = JsonConvert.DeserializeObject<byte[]>(text);

            if(keyPair == null)
            {
                return default;
            }

            Wallet wallet = new Wallet(keyPair, seedMode: SeedMode.Bip39);

            return (wallet, wallet.Account.PublicKey);
        }

        public class SettingChange
        {
            public string Path { get; set; }
            public string Setting { get; set; }
            public object OldValue { get; set; }
            public object NewValue { get; set; }
        }
    }

    public interface ISettingInfo
    {
        public string Name { get; }
        public string Description { get; }
        public bool Display { get; }
    }
}
