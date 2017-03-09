using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Hangfire.StackTrace
{
    /// <summary>
    /// Type cache to lookup type names across all assemblies
    /// </summary>
    internal static class TypeCache
    {
        private static readonly ConcurrentDictionary<AssemblyName, Assembly> _assemblies =
            new ConcurrentDictionary<AssemblyName, Assembly>(AssemblyNameComparer.Instance);

        private static readonly ConcurrentDictionary<string, Type> _types =
            new ConcurrentDictionary<string, Type>();

        private static void LoadTypes(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                _types.TryAdd(type.FullName, type);

                if (type.IsNested)
                {
                    // make nested types also resolvable by name with a dot separator,
                    // as they appear in the stack trace
                    _types.TryAdd(type.FullName.Replace('+', '.'), type);
                }
            }
        }
        
        /// <summary>
        /// Loads assembly, its types and referenced assemblies to cache.
        /// </summary>
        /// <param name="assembly">Assembly to load</param>
        public static void LoadAssembly(Assembly assembly)
        {
            if (assembly == null)
                throw new ArgumentNullException(nameof(assembly));

            if (!_assemblies.TryAdd(assembly.GetName(), assembly))
            {
                // this assembly was already added to cache
                return;
            }
            
            LoadTypes(assembly);
            
            foreach (var refName in assembly.GetReferencedAssemblies())
            {
                if (_assemblies.ContainsKey(refName)) continue;

                try
                {
                    var refAssembly = Assembly.Load(refName);

                    LoadAssembly(refAssembly);
                }
                catch
                {
                    // ignore load errors
                }
            }
        }

        /// <summary>
        /// Resolves type by its name.
        /// </summary>
        /// <param name="typeName">Type name</param>
        /// <returns>Resolved type</returns>
        public static Type GetType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                throw new ArgumentNullException(nameof(typeName));

            Type type;
            if (_types.TryGetValue(typeName, out type))
                return type;

            return null;
        }
    }
}
