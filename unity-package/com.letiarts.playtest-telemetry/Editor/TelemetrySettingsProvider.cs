using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LetiArts.PlaytestTelemetry.Editor
{
    /// <summary>Project Settings > Playtest Telemetry.</summary>
    internal static class TelemetrySettingsProvider
    {
        static string testResult;
        static bool testRunning;
        static List<ValidationMessage> validation;

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider("Project/Playtest Telemetry", SettingsScope.Project)
            {
                label = "Playtest Telemetry",
                keywords = new HashSet<string> { "telemetry", "playtest", "logging", "crash", "performance" },
                guiHandler = OnGUI,
            };
        }

        static void OnGUI(string searchContext)
        {
            TelemetrySettings settings = TelemetryEditorUtil.LoadSettings();
            if (settings == null)
            {
                EditorGUILayout.HelpBox("No settings asset yet. It is stored at " + TelemetryEditorUtil.AssetPath + " so it loads at runtime.", MessageType.Info);
                if (GUILayout.Button("Create settings")) TelemetryEditorUtil.GetOrCreateSettings();
                return;
            }

            var so = new SerializedObject(settings);
            so.Update();
            EditorGUI.BeginChangeCheck();
            DrawProperties(so);
            if (EditorGUI.EndChangeCheck())
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(settings);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scripting define", EditorStyles.boldLabel);
            bool hasDefine = TelemetryEditorUtil.HasDefineOnActiveTarget();
            EditorGUILayout.LabelField(TelemetryEditorUtil.Define, hasDefine ? "set for the active build target" : "not set");
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(hasDefine ? "Remove define" : "Add define"))
                {
                    TelemetryEditorUtil.SetDefineOnActiveTarget(!hasDefine);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Checks", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Validate setup"))
                {
                    validation = TelemetryEditorUtil.Validate(
                        settings, EditorUserBuildSettings.activeBuildTarget,
                        EditorUserBuildSettings.development, hasDefine);
                }
                using (new EditorGUI.DisabledScope(testRunning || !settings.IsConfigured))
                {
                    if (GUILayout.Button(testRunning ? "Sending..." : "Send test event"))
                    {
                        testRunning = true;
                        testResult = null;
                        TestEventSender.Send(settings, message =>
                        {
                            testRunning = false;
                            testResult = message;
                            SettingsService.NotifySettingsProviderChanged();
                        });
                    }
                }
            }

            if (validation != null)
            {
                foreach (ValidationMessage m in validation) EditorGUILayout.HelpBox(m.Text, m.Type);
            }
            if (testResult != null)
            {
                EditorGUILayout.HelpBox(testResult, testResult.StartsWith("OK") ? MessageType.Info : MessageType.Error);
            }
        }

        static void DrawProperties(SerializedObject so)
        {
            SerializedProperty prop = so.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (prop.propertyPath == "m_Script") continue;
                bool isKey = prop.propertyPath == "apiKey";
                if (isKey)
                {
                    prop.stringValue = EditorGUILayout.PasswordField(new GUIContent("Api Key", "Sent as the X-Telemetry-Key header."), prop.stringValue);
                }
                else
                {
                    EditorGUILayout.PropertyField(prop, true);
                }
            }
        }
    }
}
