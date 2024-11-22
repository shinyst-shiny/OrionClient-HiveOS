using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.SettingsData
{
    public class SettingDetailsAttribute : Attribute
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public SettingsValidator Validator { get; set; }

        public SettingDetailsAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }

    public abstract class SettingValidatorAttribute : Attribute
    {
        public abstract bool Validate(object data);
    }

    public class ThreadValidatorAttribute : SettingValidatorAttribute
    {
        public override bool Validate(object data)
        {
            if(data == null)
            {
                return false;
            }

            if(!int.TryParse(data.ToString(), out int totalThreads))
            {
                return false;
            }

            return totalThreads > 0 && totalThreads <= Environment.ProcessorCount;
        }
    }

    public class OptionSettingValidation<T> : SettingValidatorAttribute
    {
        public T[] Options;

        public OptionSettingValidation(params T[] options)
        {
            Options = options;
        }

        public override bool Validate(object data)
        {
            return Options.Contains((T)data);
        }
    }
}
