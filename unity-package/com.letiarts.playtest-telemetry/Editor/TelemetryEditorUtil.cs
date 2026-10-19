using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace LetiArts.PlaytestTelemetry.Editor
{
    internal struct ValidationMessage
    {
        public MessageType Type;
        public string Text;
    }

    internal static class TelemetryEditorUtil
    {
        public const string Define = "PLAYTEST_TELEMETRY";
        public const string AssetPath = "Assets/Resources/" + TelemetrySettings.ResourceName + ".asset";
        const string IosPluginPath = "Packages/com.letiarts.playtest-telemetry/Runtime/Plugins/iOS/PTThermal.mm";

        // ------------------------------------------------------------------ settings asset

        public static TelemetrySettings LoadSettings()
        {
            return AssetDatabase.LoadAssetAtPath<TelemetrySettings>(AssetPath);
        }

        public static TelemetrySettings GetOrCreateSettings()
        {
            TelemetrySettings settings = LoadSettings();
            if (settings != null) return settings;

            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
            settings = ScriptableObject.CreateInstance<TelemetrySettings>();
            settings.deviceIdSalt = Guid.NewGuid().ToString("N"); // generated once per project
            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }

        // ------------------------------------------------------------------ scripting define

        static NamedBuildTarget ActiveTarget()
        {
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            return NamedBuildTarget.FromBuildTargetGroup(group);
        }

        public static bool HasDefine(NamedBuildTarget target)
        {
            return new List<string>(PlayerSettings.GetScriptingDefineSymbols(target).Split(';')).Contains(Define);
        }

        public static bool HasDefineOnActiveTarget()
        {
            return HasDefine(ActiveTarget());
        }

        public static void SetDefineOnActiveTarget(bool enabled)
        {
            NamedBuildTarget target = ActiveTarget();
            var symbols = new List<string>(PlayerSettings.GetScriptingDefineSymbols(target).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            bool has = symbols.Contains(Define);
            if (enabled && !has) symbols.Add(Define);
            else if (!enabled && has) symbols.Remove(Define);
            else return;
            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", symbols));
        }

        [MenuItem("Tools/Playtest Telemetry/Add " + Define + " define")]
        static void AddDefine()
        {
            SetDefineOnActiveTarget(true);
            Debug.Log("[PlaytestTelemetry] Added " + Define + " for " + ActiveTarget().TargetName);
        }

        [MenuItem("Tools/Playtest Telemetry/Remove " + Define + " define")]
        static void RemoveDefine()
        {
            SetDefineOnActiveTarget(false);
            Debug.Log("[PlaytestTelemetry] Removed " + Define + " for " + ActiveTarget().TargetName);
        }

        [MenuItem("Tools/Playtest Telemetry/Open settings")]
        static void OpenSettings()
        {
            SettingsService.OpenProjectSettings("Project/Playtest Telemetry");
        }

        // ------------------------------------------------------------------ validation

        public static List<ValidationMessage> Validate(TelemetrySettings settings, BuildTarget buildTarget, bool developmentBuild, bool hasDefine)
        {
            var messages = new List<ValidationMessage>();
            void Add(MessageType type, string text) { messages.Add(new ValidationMessage { Type = type, Text = text }); }

            if (settings == null)
            {
                Add(MessageType.Error, "No settings asset. Click 'Create settings' in Project Settings > Playtest Telemetry.");
                return messages;
            }

            if (string.IsNullOrWhiteSpace(settings.endpointUrl)) Add(MessageType.Error, "Endpoint URL is empty.");
            else if (!settings.endpointUrl.StartsWith("https://")) Add(MessageType.Warning, "Endpoint URL is not https://. iOS and recent Android block cleartext HTTP by default.");

            if (string.IsNullOrWhiteSpace(settings.apiKey)) Add(MessageType.Error, "API key is empty.");
            if (string.IsNullOrEmpty(settings.deviceIdSalt)) Add(MessageType.Warning, "Device ID salt is empty. Device IDs would be unsalted hashes.");

            if (hasDefine && !developmentBuild)
            {
                Add(MessageType.Warning, Define + " is set but this is not a Development build: telemetry will be ACTIVE for players of this build. Remove the define for production builds.");
            }
            if (!hasDefine && (settings.enabledInEditor || settings.enabledInDevelopmentBuilds))
            {
                Add(MessageType.Info, Define + " is not set for the active build target. Telemetry still runs per the 'Enabled in' settings, but PlaytestTelemetry.* calls in your game code are compiled out. Use Tools > Playtest Telemetry > Add define.");
            }
            if (!hasDefine && !settings.enabledInEditor && !settings.enabledInDevelopmentBuilds)
            {
                Add(MessageType.Info, "Telemetry is define-only and the define is not set: it is fully off.");
            }

            if (buildTarget == BuildTarget.iOS)
            {
                var importer = AssetImporter.GetAtPath(IosPluginPath) as PluginImporter;
                if (importer == null) Add(MessageType.Warning, "iOS thermal plugin (PTThermal.mm) not found in the package; thermal state will be missing on iOS.");
                else if (!importer.GetCompatibleWithPlatform(BuildTarget.iOS)) Add(MessageType.Warning, "PTThermal.mm is not enabled for iOS in its import settings.");
            }

            if (messages.Count == 0) Add(MessageType.Info, "Setup looks good.");
            return messages;
        }
    }
}
