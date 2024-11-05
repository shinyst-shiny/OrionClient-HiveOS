using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace OrionClientLib.Pools.Models
{
    internal class PoolSettings
    {
        protected readonly string _folder = Path.Combine(AppContext.BaseDirectory, "pool_data");
        protected string _filePath => Path.Combine(_folder, $"{GetType().Name}_data.json");

        public async Task<bool> LoadAsync()
        {
            if (!File.Exists(_filePath))
            {
                return false;
            }

            var s = await File.ReadAllTextAsync(_filePath);
            var t = JsonConvert.DeserializeObject(s, GetType());

            if (t == null)
            {
                return false;
            }

            PropertyInfo[] properties = GetType().GetProperties();

            foreach (PropertyInfo property in properties)
            {
                var oldValue = property.GetValue(t);
                property.SetValue(this, oldValue);
            }

            return true;
        }

        public Task SaveAsync()
        {
            Directory.CreateDirectory(_folder);

            return File.WriteAllTextAsync(_filePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
