using UnityEngine;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>Reports the device thermal state as a string, or null when unavailable.</summary>
    public interface IThermalProvider
    {
        string GetThermalState();
    }

    internal static class ThermalProviderFactory
    {
        public static IThermalProvider Create()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidThermalProvider();
#elif UNITY_IOS && !UNITY_EDITOR
            return new IosThermalProvider();
#else
            return null;
#endif
        }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>PowerManager.getCurrentThermalStatus() (API 29+). The Java object is cached; any failure disables the provider.</summary>
    internal sealed class AndroidThermalProvider : IThermalProvider
    {
        static readonly string[] Names = { "None", "Light", "Moderate", "Severe", "Critical", "Emergency", "Shutdown" };

        AndroidJavaObject powerManager;
        bool unavailable;

        public string GetThermalState()
        {
            if (unavailable) return null;
            try
            {
                if (powerManager == null)
                {
                    using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                    {
                        if (version.GetStatic<int>("SDK_INT") < 29)
                        {
                            unavailable = true;
                            return null;
                        }
                    }
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    {
                        powerManager = activity.Call<AndroidJavaObject>("getSystemService", "power");
                    }
                    if (powerManager == null)
                    {
                        unavailable = true;
                        return null;
                    }
                }
                int status = powerManager.Call<int>("getCurrentThermalStatus");
                return status >= 0 && status < Names.Length ? Names[status] : "Unknown";
            }
            catch (System.Exception)
            {
                unavailable = true;
                return null;
            }
        }
    }
#endif

#if UNITY_IOS && !UNITY_EDITOR
    /// <summary>NSProcessInfo.thermalState through Plugins/iOS/PTThermal.mm.</summary>
    internal sealed class IosThermalProvider : IThermalProvider
    {
        [DllImport("__Internal")]
        static extern int PT_GetThermalState();

        static readonly string[] Names = { "Nominal", "Fair", "Serious", "Critical" };
        bool unavailable;

        public string GetThermalState()
        {
            if (unavailable) return null;
            try
            {
                int state = PT_GetThermalState();
                return state >= 0 && state < Names.Length ? Names[state] : "Unknown";
            }
            catch (System.Exception)
            {
                unavailable = true;
                return null;
            }
        }
    }
#endif
}
