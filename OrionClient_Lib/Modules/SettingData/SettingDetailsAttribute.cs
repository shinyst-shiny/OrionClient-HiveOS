using OrionClientLib.Hashers;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Modules.SettingsData
{
    public class SettingDetailsAttribute : Attribute
    {
        public string Name { get; private set; }
        public string Description { get; private set; }
        public bool Display { get; private set; }

        public SettingDetailsAttribute(string name, string description, bool display = true)
        {
            Name = name;
            Description = description;
            Display = display;
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

            return totalThreads >= 0 && totalThreads <= Environment.ProcessorCount;
        }
    }

    public abstract class TypeValidator : SettingValidatorAttribute
    {
        public List<ISettingInfo> Options;
    }

    public class TypeValidator<T> : TypeValidator where T : ISettingInfo
    {
        public TypeValidator()
        {
            Options = GetExtendedClasses<T>().Where(x => x.Display).Cast<ISettingInfo>().ToList();
        }

        public override bool Validate(object data)
        {
            return Options.Contains((T)data);
        }

        private List<T> GetExtendedClasses<T>(params object[] constructorArgs) 
        {
            List<T> objects = new List<T>();

            foreach (Type type in Assembly.GetAssembly(typeof(T)).GetTypes().Where(myType => myType.IsClass && !myType.IsAbstract && myType.IsSubclassOf(typeof(T))))
            {
                objects.Add((T)Activator.CreateInstance(type, constructorArgs));
            }

            return objects;
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
