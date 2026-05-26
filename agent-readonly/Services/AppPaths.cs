using System;
using System.IO;
using System.Reflection;

namespace AgentReadonly.Services
{
    public static class AppPaths
    {
        public static string ExecutableDirectory
        {
            get
            {
                string location = Assembly.GetExecutingAssembly().Location;
                return Path.GetDirectoryName(location) ?? AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        public static string ApiKeyPath
        {
            get { return Path.Combine(ExecutableDirectory, ".openai-api-key"); }
        }

        public static string ContextPath
        {
            get { return Path.Combine(ExecutableDirectory, "CONTEXT.md"); }
        }

        public static string SettingsPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TatoAgentReadonly");
                return Path.Combine(dir, "settings.json");
            }
        }
    }
}
