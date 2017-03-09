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
using System.Text.RegularExpressions;

namespace Hangfire.StackTrace
{
    internal class FailedStateRenderer
    {
        private static readonly FieldInfo _page = typeof(HtmlHelper).GetTypeInfo().GetDeclaredField("_page");

        private static readonly object _lockObject = new object();
        private static PropertyInfo _jobId; // = typeof(JobDetailsPage).GetTypeInfo().GetDeclaredProperty("JobId");

        private static readonly Func<string, string> Environment_GetResourceString;

        static FailedStateRenderer()
        {
            var grs = typeof(Environment)
                .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(x => x.Name == "GetResourceString" && x.GetParameters().Length == 1)
                .SingleOrDefault();
            
            Environment_GetResourceString = grs?.CreateDelegate(typeof(Func<string, string>)) as Func<string, string>;
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

            // Set the same culture as it was the moment the exception occured

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
                    $"<pre class=\"stack-trace\">{RenderStackTrace(jobData, stateData["ExceptionDetails"])}</pre>");
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
                TypeCache.LoadAssembly(jobData.Job.Type.GetTypeInfo().Assembly);
            }
            
            var endOfInnerExceptionStack = "End of inner exception stack trace";
            var endStackTraceFromPreviousThrow = "End of stack trace from previous location where exception was thrown";
            var at = "at";
            var inFileLineNumber = "in {0}:line {1}";

            if (Environment_GetResourceString != null)
            {
                endOfInnerExceptionStack = Environment_GetResourceString("Exception_EndOfInnerExceptionStack");
                endStackTraceFromPreviousThrow = Environment_GetResourceString("Exception_EndStackTraceFromPreviousThrow");
                at = Environment_GetResourceString("Word_At");
                inFileLineNumber = Environment_GetResourceString("StackTrace_InFileLineNumber");
            }

            var buffer = new StringBuilder(stackTrace.Length * 3);

            using (var reader = new StringReader(stackTrace))
            using (var writer = new StringWriter(buffer))
            {
                string line;
                while (null != (line = reader.ReadLine()))
                {
                    StackFrame frame;
                    if (!StackFrame.TryParse(line, out frame))
                    {
                        // Failed to parse this line as a stack frame.
                        // This may be exception message, inner stack trace separator etc.

                        // Write line "as is"
                        writer.WriteLine(WebUtility.HtmlDecode(line));
                        continue;
                    }

                    string stateMachineMethod = null;

                    var type = TypeCache.GetType(frame.TypeName);
                    if (type != null)
                    {
                        MethodBase[] methods = null;

                        // exclude some unwanted types early

                        if (typeof(INotifyCompletion).IsAssignableFrom(type))
                        {
                            // TaskAwaiter, ConfiguredTaskAwaitable etc.
                            continue;
                        }

                        if (type.IsNested && 
                            type.GetTypeInfo().GetCustomAttribute<CompilerGeneratedAttribute>() != null)
                        {
                            // Check if the type is a state machine
                            //
                            // State machines are compiler-generated nested classes named like <xyz>d__123,
                            // where 'xyz' refers to a method of the parent class it is generated for.

                            var methodName = StackFrame.GetOriginalName(type.Name);
                            if (!string.IsNullOrEmpty(methodName))
                            {
                                // Find a method of the parent class with matchin name and state machine type.
                                // There should be a single method matching this criteria, as state machines are unique.

                                methods = type.DeclaringType.FindMethods(methodName,
                                    x => x.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType == type);

                                if (methods.Length == 1)
                                {
                                    type = type.DeclaringType;

                                    // Determine type of the state machine
                                    var attr = methods[0].GetCustomAttribute<StateMachineAttribute>();
                                    if (attr is AsyncStateMachineAttribute)
                                        stateMachineMethod = "await";
                                    else if (attr is IteratorStateMachineAttribute)
                                        stateMachineMethod = "yield";
                                }
                            }
                        }

                        // Do basic method lookup

                        if (methods == null)
                        {
                            methods = type.FindMethods(frame.MethodName, method =>
                            {
                                var fp = frame.Parameters;
                                var mp = method.GetParameters();

                                if (mp.Length != fp.Length) return false;

                                for (int i = 0; i < mp.Length; i++)
                                {
                                    var t = mp[i].ParameterType;
                                    
                                    // Mono uses full type names, while .NET uses short ones.
                                    // We need to check both, to be sure.
                                    if (t.Name != fp[i].TypeName && t.FullName != fp[i].TypeName)
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
                                .Select(p => new StackFrame.Parameter()
                                {
                                    TypeName = p.ParameterType.GetFormattedName(false),
                                    Name = p.Name
                                })
                                .ToArray();
                        }
                    }
                    
                    writer.Write(WebUtility.HtmlEncode(frame.Prefix));

                    if (stateMachineMethod != null)
                        writer.Write("<span style='color:#00f'>{0}</span> ", stateMachineMethod);

                    writer.Write("<span class='st-type'>{0}</span>.<span class='st-method'>{1}</span>", 
                                 WebUtility.HtmlEncode(frame.TypeName), WebUtility.HtmlEncode(frame.MethodName));
                    
                    writer.Write('(');
                    for (var i = 0; i < frame.Parameters.Length; i++)
                    {
                        if (i > 0)
                            writer.Write(", ");

                        writer.Write("<span class='st-param'>");
                        writer.Write("<span class='st-param-type'>{0}</span>&nbsp;", WebUtility.HtmlEncode(frame.Parameters[i].TypeName));
                        writer.Write("<span class='st-param-name'>{0}</span>", WebUtility.HtmlEncode(frame.Parameters[i].Name));
                        writer.Write("</span>");
                    }
                    writer.Write(')');

                    writer.WriteLine(WebUtility.HtmlEncode(frame.Suffix));
                }
            }

            return buffer.ToString();
        }
        
        
    }
}
