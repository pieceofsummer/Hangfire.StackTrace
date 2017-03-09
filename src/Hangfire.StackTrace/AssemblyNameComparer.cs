using System.Collections.Generic;
using System.Reflection;

namespace Hangfire.StackTrace
{
    /// <summary>
    /// Equality comparer for <see cref="AssemblyName"/>.
    /// </summary>
    internal class AssemblyNameComparer : IEqualityComparer<AssemblyName>
    {
        public static AssemblyNameComparer Instance { get; } = new AssemblyNameComparer();

        public bool Equals(AssemblyName x, AssemblyName y)
        {
            if (ReferenceEquals(x, null)) return ReferenceEquals(y, null);
            if (ReferenceEquals(y, null)) return false;

            return string.Equals(x.Name, y.Name);
        }

        public int GetHashCode(AssemblyName obj)
        {
            if (ReferenceEquals(obj, null)) return 0;

            return obj.Name.GetHashCode();
        }
    }
}
