namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// A telemetry capability. Register custom modules with <see cref="PlaytestTelemetry.RegisterModule"/>.
    /// All methods are called on the main thread. Exceptions are caught by the host; a module that fails
    /// repeatedly is disabled so it can never hurt the game.
    /// </summary>
    public interface ITelemetryModule
    {
        /// <summary>Called once. Subscribe to events and read settings here.</summary>
        void Initialize(TelemetryContext context);

        /// <summary>Called every frame with the unscaled delta time. Must not allocate heavily.</summary>
        void Tick(float unscaledDt);

        /// <summary>Called when telemetry shuts down. Unsubscribe from everything.</summary>
        void Shutdown();
    }

    /// <summary>Optional: a module that buffers data and hands it over just before each flush.</summary>
    public interface IFlushableModule
    {
        void OnFlush();
    }

    /// <summary>Optional: a module that reacts to the application being paused or quit.</summary>
    public interface IApplicationLifecycleModule
    {
        void OnPause(bool paused);
        void OnQuit();
    }
}
