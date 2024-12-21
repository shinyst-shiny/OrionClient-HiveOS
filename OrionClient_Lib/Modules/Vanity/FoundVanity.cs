using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.Vanity
{
    public class VanityTracker
    {
        public List<FoundVanity> Vanities { get; private set; } = new List<FoundVanity>();
        private HashSet<string> _uniqueVanities = new HashSet<string>();


        public int Total => Vanities.Count;
        public int UniqueCount => _uniqueVanities.Count;
        public int Searching { get; set; }

        public void Add(FoundVanity vanity)
        {
            lock (Vanities)
            {
                Vanities.Add(vanity);
                _uniqueVanities.Add(vanity.VanityText);
            }
        }
    }

    public class FoundVanity
    {
        public string PublicKey { get; set; }
        public string PrivateKey { get; set; }
        public string VanityText { get; set; }

        public override int GetHashCode()
        {
            return VanityText?.GetHashCode() ?? base.GetHashCode();
        }

        public override bool Equals(object? obj)
        {
            if(obj is FoundVanity foundVanity)
            {
                return foundVanity.VanityText == VanityText;
            }

            return base.Equals(obj);
        }
    }
}
