using Newtonsoft.Json;
using OrionClientLib.Modules.Models;
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

        public int CPUThreads { get; set; } = 1;
        public string Pool { get; set; }
        public string CPUHasher { get; set; } = "Stock";
        public string GPUHasher { get; set; } = "Disabled";
        public List<int> GPUDevices { get; set; } = new List<int>();

        public bool HasPrivateKey { get; set; }
        public string KeyFile { get; set; }
        public string PublicKey { get; set; }

        public bool EnableDebugging { get; set; }

        public static async Task<Settings> LoadAsync()
        {
            if (!File.Exists(FilePath))
            {
                Settings settings = new Settings();
                settings.CPUThreads = Environment.ProcessorCount;

                return settings;
            }

            var s = await File.ReadAllTextAsync(FilePath);
            var t = JsonConvert.DeserializeObject<Settings>(s);

            if (t == null)
            {
                throw new Exception("Failed to read setting file");
            }

            return t;
        }

        public async Task ReloadAsync()
        {
            Settings oldSettings = await LoadAsync();

            PropertyInfo[] properties = typeof(Settings).GetProperties();

            foreach(PropertyInfo property in properties)
            {
                var oldValue = property.GetValue(oldSettings);
                property.SetValue(this, oldValue);
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
    }

    public class JsonCommentConverter : JsonConverter
    {
        private readonly string _comment;
        public JsonCommentConverter(string comment)
        {
            _comment = comment;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(value);
            writer.WriteComment(_comment); // append comment
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        public override bool CanConvert(Type objectType) => true;
        public override bool CanRead => false;
    }
}
