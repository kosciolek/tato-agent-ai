using System;
using System.IO;
using System.Web.Script.Serialization;

namespace AgentReadonly.Services
{
    public class AppSettings
    {
        private const string DefaultModel = "gpt-5.5";
        private const double DefaultFontSize = 18.0;

        public string ProjectRoot { get; set; }
        public string Model { get; set; }
        public double FontSize { get; set; }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings
            {
                Model = DefaultModel,
                FontSize = DefaultFontSize
            };

            try
            {
                if (!File.Exists(AppPaths.SettingsPath))
                {
                    AppLog.Info("Settings file not found, using defaults: " + AppPaths.SettingsPath);
                    return settings;
                }

                string json = File.ReadAllText(AppPaths.SettingsPath);
                AppSettings loaded = new JavaScriptSerializer().Deserialize<AppSettings>(json);
                if (loaded == null)
                    return settings;

                if (!string.IsNullOrWhiteSpace(loaded.ProjectRoot))
                    settings.ProjectRoot = loaded.ProjectRoot;
                if (!string.IsNullOrWhiteSpace(loaded.Model))
                    settings.Model = loaded.Model.Trim();
                if (loaded.FontSize >= 12 && loaded.FontSize <= 42)
                    settings.FontSize = loaded.FontSize;
            }
            catch (Exception ex)
            {
                AppLog.Error("Settings load failed, using defaults: " + AppPaths.SettingsPath, ex);
                return settings;
            }

            AppLog.Info("Settings loaded: path=" + AppPaths.SettingsPath + " project_root=" + (settings.ProjectRoot ?? "") + " model=" + settings.Model + " font_size=" + settings.FontSize);
            return settings;
        }

        public void Save()
        {
            string dir = Path.GetDirectoryName(AppPaths.SettingsPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string json = new JavaScriptSerializer().Serialize(this);
            File.WriteAllText(AppPaths.SettingsPath, json);
            AppLog.Info("Settings saved: path=" + AppPaths.SettingsPath + " project_root=" + (ProjectRoot ?? "") + " model=" + Model + " font_size=" + FontSize);
        }

        public static string ReadApiKey()
        {
            if (!File.Exists(AppPaths.ApiKeyPath))
                throw new FileNotFoundException("Missing .openai-api-key next to agent-readonly.exe.", AppPaths.ApiKeyPath);

            string key = File.ReadAllText(AppPaths.ApiKeyPath).Trim();
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException(".openai-api-key is empty.");

            AppLog.Info("API key loaded: path=" + AppPaths.ApiKeyPath + " chars=" + key.Length);
            return key;
        }

        public static string ReadContext()
        {
            if (!File.Exists(AppPaths.ContextPath))
            {
                AppLog.Info("Context file not found: " + AppPaths.ContextPath);
                return "";
            }
            string context = File.ReadAllText(AppPaths.ContextPath);
            AppLog.Info("Context loaded: path=" + AppPaths.ContextPath + " chars=" + context.Length);
            return context;
        }
    }
}
