using System;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Wraps a module so that nothing it does can throw into the game. A module that fails
    /// <see cref="MaxConsecutiveFailures"/> times in a row is disabled.
    /// </summary>
    internal sealed class ModuleHost
    {
        public const int MaxConsecutiveFailures = 5;

        public readonly ITelemetryModule Module;
        int failures;

        public bool Disabled { get; private set; }
        public string Name => Module.GetType().Name;

        public ModuleHost(ITelemetryModule module)
        {
            Module = module;
        }

        public void Initialize(TelemetryContext context)
        {
            try
            {
                Module.Initialize(context);
            }
            catch (Exception e)
            {
                Disable("Initialize", e);
            }
        }

        public void Tick(float dt)
        {
            if (Disabled) return;
            try
            {
                Module.Tick(dt);
                failures = 0;
            }
            catch (Exception e)
            {
                Failed("Tick", e);
            }
        }

        public void Flush()
        {
            if (Disabled) return;
            var flushable = Module as IFlushableModule;
            if (flushable == null) return;
            try
            {
                flushable.OnFlush();
            }
            catch (Exception e)
            {
                Failed("OnFlush", e);
            }
        }

        public void Pause(bool paused)
        {
            var lifecycle = Module as IApplicationLifecycleModule;
            if (Disabled || lifecycle == null) return;
            try { lifecycle.OnPause(paused); } catch (Exception e) { Failed("OnPause", e); }
        }

        public void Quit()
        {
            var lifecycle = Module as IApplicationLifecycleModule;
            if (lifecycle == null) return;
            try { lifecycle.OnQuit(); } catch (Exception e) { PtLog.Warn(Name + ".OnQuit failed: " + e.Message); }
        }

        public void Shutdown()
        {
            try { Module.Shutdown(); } catch (Exception e) { PtLog.Warn(Name + ".Shutdown failed: " + e.Message); }
        }

        void Failed(string where, Exception e)
        {
            failures++;
            PtLog.Warn(Name + "." + where + " failed (" + failures + "/" + MaxConsecutiveFailures + "): " + e.Message);
            if (failures >= MaxConsecutiveFailures) Disable(where, e);
        }

        void Disable(string where, Exception e)
        {
            Disabled = true;
            PtLog.Warn(Name + " disabled after failure in " + where + ": " + e.Message);
            try { Module.Shutdown(); } catch (Exception) { /* already failing */ }
        }
    }
}
