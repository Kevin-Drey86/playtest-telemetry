using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LetiArts.PlaytestTelemetry.Editor
{
    /// <summary>Build-time checks. Only ever warns; it never fails a build.</summary>
    internal sealed class TelemetryBuildProcessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            BuildSummary summary = report.summary;
            bool development = (summary.options & BuildOptions.Development) != 0;
            NamedBuildTarget target = NamedBuildTarget.FromBuildTargetGroup(summary.platformGroup);
            bool hasDefine = TelemetryEditorUtil.HasDefine(target);

            TelemetrySettings settings = TelemetryEditorUtil.LoadSettings();
            if (!hasDefine && settings == null) return; // telemetry not in use

            foreach (ValidationMessage m in TelemetryEditorUtil.Validate(settings, summary.platform, development, hasDefine))
            {
                if (m.Type == MessageType.Warning) Debug.LogWarning("[PlaytestTelemetry] " + m.Text);
                else if (m.Type == MessageType.Error && (hasDefine || development)) Debug.LogWarning("[PlaytestTelemetry] " + m.Text);
            }
        }
    }
}
