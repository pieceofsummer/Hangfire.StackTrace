using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hangfire.StackTrace
{
    public class StackFrame
    {
        private static readonly Parameter[] EmptyParameters = new Parameter[0];

        public class Parameter
        {
            public string TypeName { get; set; }
            
            public string Name { get; set; }
        }
        
        public string Prefix { get; set; }

        public string TypeName { get; set; }
        
        public string MethodName { get; set; }
        
        public Parameter[] Parameters { get; set; }

        public string Suffix { get; set; }

        private static readonly string Literal = @"[^\s\.+`\[]+";
        private static readonly string MemberName = $@"{Literal}(`\d+)?(\[{Literal}(,\s*{Literal})*\])?";
        private static readonly string FullTypeName = $@"({Literal}\.)*{MemberName}(\.{MemberName})*";
        private static readonly string MethodParameter = $@"(?<paramtype>{FullTypeName})\s+(?<paramname>{Literal})";
        
        private static readonly Regex StackFrameLine = new Regex(
            $@"^(?<prefix>\s*\w+\s+)
                (?<type>{FullTypeName})
                \.
                (?<method>{MemberName})
                \(
                    (?<params>{MethodParameter}
                         (,\s*{MethodParameter})*)?
                \)
                (?<suffix>\s+.+)?
                \s*$",
            RegexOptions.ExplicitCapture | 
            RegexOptions.IgnorePatternWhitespace | 
            RegexOptions.CultureInvariant | 
            RegexOptions.Compiled);

        private static readonly Regex SpecialName = new Regex(
            $@"^\<(?<name>{Literal})\>{MemberName}$", 
            RegexOptions.ExplicitCapture | 
            RegexOptions.IgnorePatternWhitespace | 
            RegexOptions.CultureInvariant | 
            RegexOptions.Compiled);

        public static bool TryParse(string line, out StackFrame stackFrame)
        {
            stackFrame = null;
            if (string.IsNullOrEmpty(line)) return false;

            var match = StackFrameLine.Match(line);
            if (!match.Success) return false;

            stackFrame = new StackFrame()
            {
                Prefix = match.Groups["prefix"].Value,
                TypeName = match.Groups["type"].Value,
                MethodName = match.Groups["method"].Value,
                Parameters = EmptyParameters,
                Suffix = match.Groups["suffix"].Value
            };

            if (match.Groups["params"].Success)
            {
                var paramTypes = match.Groups["paramtype"].Captures;
                var paramNames = match.Groups["paramname"].Captures;

                var parameters = new Parameter[paramTypes.Count];
                for (int i = 0; i < paramTypes.Count; i++)
                {
                    parameters[i] = new Parameter()
                    {
                        TypeName = paramTypes[i].Value,
                        Name = paramNames[i].Value
                    };
                }

                stackFrame.Parameters = parameters;
            }
            
            return true;
        }

        public static string GetOriginalName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));

            var match = SpecialName.Match(name);

            return match.Success ? match.Groups["name"].Value : null;
        }
    }
}
