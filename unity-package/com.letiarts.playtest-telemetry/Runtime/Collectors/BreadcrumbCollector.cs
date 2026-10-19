namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Owns the breadcrumb ring buffer lifecycle. Other modules (logs, scenes) and the public API feed
    /// it; it is only ever attached to Error/Exception events and bug reports, never sent on its own.
    /// </summary>
    internal sealed class BreadcrumbCollector : ITelemetryModule
    {
        TelemetryContext context;

        public void Initialize(TelemetryContext ctx)
        {
            context = ctx;
            ctx.Client.Breadcrumbs.Resize(ctx.Settings.breadcrumbBufferSize);
            ctx.Client.Breadcrumbs.Enabled = true;
        }

        public void Tick(float unscaledDt) { }

        public void Shutdown()
        {
            if (context != null) context.Client.Breadcrumbs.Enabled = false;
        }
    }
}
