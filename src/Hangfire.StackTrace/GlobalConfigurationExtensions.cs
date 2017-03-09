using Hangfire.Dashboard;
using Hangfire.States;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Hangfire.StackTrace
{
    /// <summary>
    /// Provides extension methods to setup Hangfire.StackTrace.
    /// </summary>
    public static class GlobalConfigurationExtensions
    {
        /// <summary>
        /// Configures Hangfire to use Console.
        /// </summary>
        /// <param name="configuration">Global configuration</param>
        public static IGlobalConfiguration UseStackTrace(this IGlobalConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            
            JobHistoryRenderer.Register(FailedState.StateName, new FailedStateRenderer().Render);
            
            return configuration;
        }
    }
}
