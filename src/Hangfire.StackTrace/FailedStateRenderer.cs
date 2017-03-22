using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hangfire.Dashboard;
using System.Reflection;
using System.Diagnostics;
using Hangfire.Storage;
using System.Net;
using System.Text;
using System.IO;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;

namespace Hangfire.StackTrace
{
    internal class FailedStateRenderer
    {
        private static readonly FieldInfo _page = typeof(HtmlHelper).GetTypeInfo().GetDeclaredField("_page");

        private static readonly object _lockObject = new object();
        private static PropertyInfo _jobId; // = typeof(JobDetailsPage).GetTypeInfo().GetDeclaredProperty("JobId");

        private static Func<string, string> GetResourceString;

        private static readonly Regex FileAndLine = new Regex(
            @"^\s+( # .NET style:
                (?<in>\w+)\s+(?<file>.+):\w+\s+(?<line>\d+)
                  | # Mono style:
                (\[(?<addr>.+)\]\s+)?
                (?<in>\w+)\s+\<(?<file>.+)\>:(?<line>\d+)       
            )\s*$",
            RegexOptions.ExplicitCapture | 
            RegexOptions.IgnorePatternWhitespace | 
            RegexOptions.CultureInvariant | 
            RegexOptions.Compiled
        );

        static FailedStateRenderer()
        {
            var method = typeof(Environment).GetMethod("GetResourceFromDefault", BindingFlags.NonPublic | BindingFlags.Static);
            
            GetResourceString = method?.CreateDelegate(typeof(Func<string, string>)) as Func<string, string>;
        }

        private static void SetCurrentCulture(CultureInfo value)
        {
#if NET45
            System.Threading.Thread.CurrentThread.CurrentCulture = value;
#else
            CultureInfo.CurrentCulture = value;
#endif
        }

        private static void SetCurrentUICulture(CultureInfo value)
        {
#if NET45
            System.Threading.Thread.CurrentThread.CurrentUICulture = value;
#else
            CultureInfo.CurrentUICulture = value;
#endif
        }

        private readonly bool _separateStackTraces;

        public FailedStateRenderer(bool separateStackTraces = true)
        {
            _separateStackTraces = separateStackTraces;
        }

        public NonEscapedString Render(HtmlHelper html, IDictionary<string, string> stateData)
        {
            // HtmlHelper._page field contains reference to a RazorPage being rendered
            var page = (RazorPage)_page.GetValue(html);
            Debug.Assert(page != null);

            // JobDetailsPage.JobId contains identifier of a parent job
            // Unfortunately, JobDetailsPage class itself is not public,
            // so we need to resolve it at runtime from page instance
            
            if (_jobId == null)
            {
                lock (_lockObject)
                {
                    if (_jobId == null)
                    {
                        _jobId = page.GetType().GetTypeInfo().GetDeclaredProperty("JobId");
                    }
                }
            }
            
            var jobId = (string)_jobId.GetValue(page);
            Debug.Assert(!string.IsNullOrEmpty(jobId));

            // Read job information by id

            JobData jobData;
            string currentCulture, currentUICulture;

            using (var connection = page.Storage.GetConnection())
            {
                jobData = connection.GetJobData(jobId);
                currentCulture = connection.GetJobParameter(jobId, "CurrentCulture");
                currentUICulture = connection.GetJobParameter(jobId, "CurrentUICulture");
            }

            // Set the same culture as when the exception has occurred
            // (needed for a locale-specific resource lookup).

            var prevCulture = CultureInfo.CurrentCulture;
            if (!string.IsNullOrEmpty(currentCulture))
                SetCurrentCulture(new CultureInfo(currentCulture));

            var prevUICulture = CultureInfo.CurrentUICulture;
            if (!string.IsNullOrEmpty(currentUICulture))
                SetCurrentUICulture(new CultureInfo(currentUICulture));
            
            try
            {
                return new NonEscapedString(
                    $"<h4 class=\"exception-type\">{WebUtility.HtmlEncode(stateData["ExceptionType"])}</h4>" +
                    $"<p class=\"text-muted\">{WebUtility.HtmlEncode(stateData["ExceptionMessage"])}</p>" +
                    RenderStackTrace(jobData, stateData["ExceptionDetails"]));
            }
            finally
            {
                SetCurrentCulture(prevCulture);
                SetCurrentUICulture(prevUICulture);
            }
        }

        private string RenderStackTrace(JobData jobData, string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return "";

            if (jobData != null && jobData.Job != null)
            {
                // Load the assembly containing job type to make types resolvable
                try
                {
                    TypeCache.LoadAssembly(jobData.Job.Type.GetTypeInfo().Assembly);
                }
                catch
                {
                    // ignore any possible exceptions
                }
            }
            
            var endOfInnerExceptionStack = "--- End of inner exception stack trace ---";
            var endStackTraceFromPreviousThrow = "--- End of stack trace from previous location where exception was thrown ---";
            
            if (GetResourceString != null)
            {
                endOfInnerExceptionStack = GetResourceString("Exception_EndOfInnerExceptionStack");
                endStackTraceFromPreviousThrow = GetResourceString("Exception_EndStackTraceFromPreviousThrow");
            }

            var buffer = new StringBuilder(stackTrace.Length * 3);
            var skipReThrow = false;

            var processingFirstLine = true;
            StringBuilder firstLineBuffer = null;
            string[] exceptionMessages = null;
            var exceptionMessageIndex = -1;

            using (var reader = new StringReader(stackTrace))
            using (var writer = new StringWriter(buffer))
            {
                writer.WriteLine("<pre class=\"stack-trace\">");

                string line;
                while (null != (line = reader.ReadLine()))
                {
                    StackFrame frame;
                    if (!StackFrame.TryParse(line, out frame))
                    {
                        // Failed to parse this line as a stack frame.
                        // This may be exception message, inner stack trace separator etc.

                        if (processingFirstLine)
                        {
                            // Collect message lines before first stack frame
                            if (firstLineBuffer == null)
                                firstLineBuffer = new StringBuilder();
                            firstLineBuffer.AppendLine(line);
                            continue;
                        }

                        var trimmed = line.Trim();

                        if (trimmed == endOfInnerExceptionStack)
                        {
                            // This is a boundary between inner and outer exceptions.
                            if (exceptionMessageIndex >= 0)
                            {
                                exceptionMessageIndex++;
                                WriteExceptionMessage(writer, exceptionMessages[exceptionMessageIndex], exceptionMessageIndex);
                            }
                            else
                                writer.WriteLine("<i class=\"text-muted\">{0}</i>", WebUtility.HtmlEncode(line));
                            continue;
                        }

                        if (trimmed == endStackTraceFromPreviousThrow)
                        {
                            // This is a boundary between exception re-throws, re-throw statement follows.
                            // We can pretty much ignore this, to keep the stack trace cleaner. 
                            // Anyways, we're only interested in where the exception was originally thrown.
                            skipReThrow = true;
                            continue;
                        }
                                
                        // Other unknown lines are written "as is"
                        writer.WriteLine(WebUtility.HtmlEncode(line));
                        continue;
                    }

                    if (processingFirstLine)
                    {
                        if (firstLineBuffer != null)
                        {
                            // First line contains all exception types and messages:
                            // Type1: message 1 ---> Type2: message 2 ---> ...
                            // (can actually span across multiple lines if exception messages contain line breaks)
                            var firstLine = firstLineBuffer.ToString().TrimEnd('\r', '\n');

                            // We want to write each exception message before its stack trace:
                            // Type2: message 2
                            //    at ...
                            //    at ...
                            //    --- End of inner exception stack trace ---
                            // Type1: message 1
                            //    at ...
                            //    at ...

                            exceptionMessages = StackFrame.SplitExceptionMessages(firstLine);
                            
                            // Since individual exception messages may have ' ---> ' inside, 
                            // we can't be sure we've split them correctly without second check.

                            // To get the correct number, we'll count inner exception stack end marks.
                            // number of exceptions = number of inner exception ends + 1
                            var numberOfInnerExceptions = Regex.Matches(
                                stackTrace.Substring(firstLine.Length),
                                $@"^\s*{Regex.Escape(endOfInnerExceptionStack)}\s*$", 
                                RegexOptions.Multiline).Count;

                            if (exceptionMessages.Length == numberOfInnerExceptions + 1)
                            {
                                Array.Reverse(exceptionMessages);

                                // Clear already written 'pre'
                                writer.Flush();
                                buffer.Clear();

                                exceptionMessageIndex = 0;
                                WriteExceptionMessage(writer, exceptionMessages[exceptionMessageIndex], exceptionMessageIndex);
                            }
                            else
                            {
                                // Numbers don't match, forget it!
                                // Write the entire first line intact, as it used to be.
                                writer.WriteLine(WebUtility.HtmlEncode(firstLine));
                            }
                        }

                        processingFirstLine = false;
                    }

                    string stateMachineMethod = null;

                    var type = TypeCache.GetType(frame.TypeName);
                    if (type != null)
                    {
                        MethodBase[] methods = null;

                        // Exclude some unwanted types:

                        if (skipReThrow)
                        {
                            skipReThrow = false;

                            if (typeof(ExceptionDispatchInfo) == type && frame.MethodName == "Throw")
                            {
                                // Re-throw statement, following the re-throw boundary
                                skipReThrow = false;
                                continue;
                            }
                        }

                        if (typeof(INotifyCompletion).IsAssignableFrom(type))
                        {
                            // TaskAwaiter, ConfiguredTaskAwaitable etc.
                            continue;
                        }

                        if (type.IsNested && type.GetTypeInfo().GetCustomAttribute<CompilerGeneratedAttribute>() != null)
                        {
                            // Check if the type is a state machine.
                            // State machines are compiler-generated nested classes named like <xyz>d__123,
                            // where 'xyz' refers to a method of the parent class it was generated for.

                            var methodName = StackFrame.DecodeOriginalName(type.Name);
                            if (!string.IsNullOrEmpty(methodName))
                            {
                                // Find a method of the parent class with matching name and state machine type.
                                // There should be a single method matching this criteria, as state machines are unique.

                                methods = type.DeclaringType.FindMethods(methodName,
                                    x => x.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType == type);

                                if (methods.Length == 1)
                                {
                                    type = type.DeclaringType;

                                    var attr = methods[0].GetCustomAttribute<StateMachineAttribute>();
                                    if (attr is AsyncStateMachineAttribute)
                                    {
                                        // Async state machine in await
                                        stateMachineMethod = "await";
                                    }
                                    else if (attr is IteratorStateMachineAttribute)
                                    {
                                        // Iterator state machine in yield
                                        stateMachineMethod = "yield";
                                    }
                                }
                            }
                        }

                        // Default method lookup, if no special one was already done

                        if (methods == null)
                        {
                            methods = type.FindMethods(frame.MethodName, method =>
                            {
                                var x = method.GetParameters();
                                var y = frame.Parameters;

                                if (x.Length != y.Length) return false;

                                for (int i = 0; i < x.Length; i++)
                                {
                                    if (x[i].ParameterType.Name != y[i].TypeName &&     // .NET style
                                        x[i].ParameterType.FullName != y[i].TypeName)   // Mono style
                                    {
                                        return false;
                                    }
                                }

                                return true;
                            });
                        }
                        
                        // Overwrite frame information with actual values

                        frame.TypeName = type.GetFormattedName();
                        
                        if (methods.Length == 1)
                        {
                            frame.MethodName = methods[0].GetFormattedName();

                            frame.Parameters = methods[0].GetParameters()
                                .Select(p => new StackFrame.Parameter(p.ParameterType.GetFormattedName(false), p.Name))
                                .ToArray();
                        }
                    }
                    
                    writer.Write(WebUtility.HtmlEncode(frame.Prefix));

                    if (stateMachineMethod != null)
                        writer.Write("<span style=\"color:#00f\">{0}</span> ", stateMachineMethod);

                    writer.Write("<span class=\"st-type\">{0}</span>.<span class=\"st-method\">{1}</span>", 
                                 WebUtility.HtmlEncode(frame.TypeName), WebUtility.HtmlEncode(frame.MethodName));
                    
                    writer.Write('(');
                    for (var i = 0; i < frame.Parameters.Length; i++)
                    {
                        if (i > 0)
                            writer.Write(", ");

                        writer.Write("<span class=\"st-param\">");
                        writer.Write("<span class=\"st-param-type\">{0}</span>&nbsp;", 
                                     WebUtility.HtmlEncode(frame.Parameters[i].TypeName));
                        writer.Write("<span class=\"st-param-name\">{0}</span>", 
                                     WebUtility.HtmlEncode(frame.Parameters[i].Name));
                        writer.Write("</span>");
                    }
                    writer.Write(')');

                    WriteFileAndLine(writer, frame.Suffix);
                }

                writer.WriteLine("</pre>");
            }

            return buffer.ToString();
        }

        private void WriteExceptionMessage(StringWriter writer, string message, int index)
        {
            if (string.IsNullOrEmpty(message))
                throw new ArgumentNullException(nameof(message));

            var p = message.IndexOf(": ");
            if (p == -1)
                throw new ArgumentException("Invalid message", nameof(message));

            var type = message.Substring(0, p);
            message = message.Substring(p + 2);

            if (index > 0)
                writer.WriteLine("</pre>");

            writer.WriteLine("<hr style=\"border-top:1px dashed #999; margin:10px 0\"/>");

            writer.WriteLine("<p>");
            writer.WriteLine("<span class=\"st-type\">{0}</span>: ", WebUtility.HtmlEncode(type));
            writer.WriteLine("<span class=\"text-muted\">{0}</span>", WebUtility.HtmlEncode(message));
            writer.WriteLine("</p>");

            writer.WriteLine("<pre class=\"stack-trace\" style=\"font-weight:normal !important\">");
        }

        private void WriteFileAndLine(StringWriter writer, string suffix)
        {
            var match = FileAndLine.Match(suffix);
            if (!match.Success)
            {
                writer.WriteLine(suffix);
                return;
            }

            var file = match.Groups["file"].Value;

            // TODO: shorten file path?

            if (match.Groups["addr"].Success)
                writer.Write(" [{0}]", WebUtility.HtmlEncode(match.Groups["addr"].Value));

            writer.WriteLine(" {0} <span class=\"st-file\">{1}</span>:<span class=\"st-line\">{2}</span>", 
                WebUtility.HtmlEncode(match.Groups["in"].Value), WebUtility.HtmlEncode(file), match.Groups["line"].Value);
        }
    }
}
