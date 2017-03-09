using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Hangfire.StackTrace
{
    internal static class ReflectionExtensions
    {
        private static void AppendGenericArguments(this StringBuilder buffer, string name, Type[] args)
        {
            var idx = name.IndexOf('`');
            if (idx > 0)
                buffer.Remove(buffer.Length - name.Length + idx, name.Length - idx);

            var divider = "<";
            foreach (var arg in args)
            {
                buffer.Append(divider).Append(GetFormattedName(arg, false));
                divider = ", ";
            }

            if (divider != "<")
                buffer.Append('>');
        }

        public static string GetFormattedName(this Type type, bool withNamespace = true)
        {
            if (type.IsGenericParameter)
                return type.Name;

            var buffer = new StringBuilder();

            if (type.IsNested)
            {
                buffer.Append(GetFormattedName(type.DeclaringType, withNamespace)).Append('.');
            }
            else if (withNamespace && !string.IsNullOrEmpty(type.Namespace))
            {
                buffer.Append(type.Namespace).Append('.');
            }

            buffer.Append(type.Name);

            var typeInfo = type.GetTypeInfo();
            if (typeInfo.IsGenericType)
            {
                buffer.AppendGenericArguments(type.Name, type.GetGenericArguments());
            }

            return buffer.ToString();
        }

        public static string GetFormattedName(this MethodBase method)
        {
            var buffer = new StringBuilder(method.Name);

            if (method.IsGenericMethod)
            {
                buffer.AppendGenericArguments(method.Name, method.GetGenericArguments());
            }

            return buffer.ToString();
        }

        public static MethodBase[] FindMethods(this Type type, string name, Func<MethodBase, bool> predicate)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));

            return type
                .GetMember(name,
                    BindingFlags.Static |
                    BindingFlags.Instance |
                    BindingFlags.NonPublic |
                    BindingFlags.Public |
                    BindingFlags.FlattenHierarchy)
                .OfType<MethodBase>()
                .Where(predicate)
                .ToArray();
        }
    }
}
