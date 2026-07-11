using System.Collections.Generic;
using System.Reflection;
using System;

namespace ET
{
    public class CodeTypes: Singleton<CodeTypes>, ISingletonAwake<Assembly[]>
    {
        private readonly Dictionary<string, Type> allTypes = new();
        // key: 特性类型, value: 标记了该特性的所有类型 如果该类型没有直接打上特性
        // 但是父类打了特性，那么这个类型也算打了这个特性
        private readonly UnOrderMultiMapSet<Type, Type> types = new();
        
        public void Awake(Assembly[] assemblies)
        {
            Dictionary<string, Type> addTypes = AssemblyHelper.GetAssemblyTypes(assemblies);
            foreach ((string fullName, Type type) in addTypes)
            {
                this.allTypes[fullName] = type;
                
                if (type.IsAbstract)
                {
                    continue;
                }
                
                // 记录所有的有BaseAttribute标记的的类型
                object[] objects = type.GetCustomAttributes(typeof(BaseAttribute), true);

                foreach (object o in objects)
                {
                    this.types.Add(o.GetType(), type);
                }
            }
        }

        public HashSet<Type> GetTypes(Type systemAttributeType)
        {
            if (!this.types.ContainsKey(systemAttributeType))
            {
                return new HashSet<Type>();
            }

            return this.types[systemAttributeType];
        }

        public Dictionary<string, Type> GetTypes()
        {
            return allTypes;
        }
        
        public Type GetType(string typeName, bool error = false)
        {
            if (this.allTypes.TryGetValue(typeName, out var type))
            {
                return type;
            }
            if (error)
                Log.Error($"没有找到这个类型: + {typeName}");
            return null;
        }
        
        public void CreateCode()
        {
            var hashSet = this.GetTypes(typeof (CodeAttribute));
            foreach (Type type in hashSet)
            {
                object obj = Activator.CreateInstance(type);
                ((ISingletonAwake)obj).Awake();
                World.Instance.AddSingleton((ASingleton)obj);
            }
        }
    }
}