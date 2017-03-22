using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hangfire.StackTrace
{
    /// <summary>
    /// Stack frame information
    /// </summary>
    internal class StackFrame
    {
        private static readonly Parameter[] EmptyParameters = new Parameter[0];

        /// <summary>
        /// Method parameter information
        /// </summary>
        public class Parameter
        {
            public string TypeName { get; }
            
            public string Name { get; }

            public Parameter(string typeName, string name)
            {
                TypeName = typeName;
                Name = name;
            }
        }
        
        /// <summary>
        /// Original line prefix (e.g. '   at ');
        /// </summary>
        public string Prefix { get; set; }

        /// <summary>
        /// Name of the type called method belongs to
        /// </summary>
        public string TypeName { get; set; }
        
        /// <summary>
        /// Called method name
        /// </summary>
        public string MethodName { get; set; }
        
        /// <summary>
        /// Method parameters 
        /// </summary>
        public Parameter[] Parameters { get; set; }

        /// <summary>
        /// Original line suffix, if any (e.g. ' in xyz.cs:line 123')
        /// </summary>
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

        /// <summary>
        /// Attepts to parse <paramref see="line" /> as a stack trace frame.
        /// </summary>
        /// <param name="line">Line to parse</param>
        /// <param name="stackFrame">Parsed stack frame</param>
        /// <returns><c>true</c> if line was successfully parsed, <c>false</c> otherwise</returns>
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
                    parameters[i] = new Parameter(paramTypes[i].Value, paramNames[i].Value);
                }

                stackFrame.Parameters = parameters;
            }
            
            return true;
        }

        private static readonly Regex SpecialName = new Regex(
            $@"^\<(?<name>{Literal})\>{MemberName}$", 
            RegexOptions.ExplicitCapture | 
            RegexOptions.IgnorePatternWhitespace | 
            RegexOptions.CultureInvariant | 
            RegexOptions.Compiled);

        /// <summary>
        /// Returns original name encoded in a special compiler-generated name (like &lt;OriginalName&gt;d__71)
        /// </summary>
        /// <param name="name">Compiler-generated name</param>
        /// <returns>Decoded name, or <c>null</c> if <paramref see="name"/> is not a valid compiler-generated name</returns>
        public static string DecodeOriginalName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));

            var match = SpecialName.Match(name);

            return match.Success ? match.Groups["name"].Value : null;
        }

        private static readonly Regex ExceptionBoundary = new Regex(
            $@"\s--->\s(?={FullTypeName}:\s)", 
            RegexOptions.ExplicitCapture | 
            RegexOptions.IgnorePatternWhitespace | 
            RegexOptions.CultureInvariant | 
            RegexOptions.Compiled);

        public static string[] SplitExceptionMessages(string line)
        {
            if (string.IsNullOrEmpty(line))
                throw new ArgumentNullException(nameof(line));

            return ExceptionBoundary.Split(line);
        }
    }
}
