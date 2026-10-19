using System;
using System.Collections.Generic;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>What a module can see and do. Passed to <see cref="ITelemetryModule.Initialize"/>.</summary>
    public sealed class TelemetryContext
    {
        internal readonly TelemetryClient Client;

        internal TelemetryContext(TelemetryClient client)
        {
            Client = client;
        }

        public TelemetrySettings Settings => Client.Settings;
        public string SessionId => Client.Session.Id;
        public string TesterAlias => Client.Session.TesterAlias;

        /// <summary>Name of the active scene; kept up to date by the scene module.</summary>
        public string CurrentScene { get; set; } = "";

        /// <summary>False while consent is required but not given: modules must not collect anything.</summary>
        public bool CanCollect => Client.CanCollect;

        /// <summary>Raised when a context value is set (value) or removed (null).</summary>
        public event Action<string, string> ContextChanged;

        /// <summary>Raised when the tester alias changes.</summary>
        public event Action<string> AliasChanged;

        public void AddBreadcrumb(string message, string category = null)
        {
            Client.Breadcrumbs.Add(category, message);
        }

        /// <summary>Sends a custom event (low priority) with the next flush.</summary>
        public void TrackEvent(string name, IDictionary<string, object> props = null)
        {
            Client.TrackEvent(name, props);
        }

        /// <summary>Asks for a flush soon (rate-limited).</summary>
        public void RequestFlush()
        {
            Client.RequestImmediateFlush();
        }

        /// <summary>A copy of the current context key/values.</summary>
        public Dictionary<string, string> SnapshotContext()
        {
            return Client.ContextValues.Snapshot();
        }

        internal void RaiseContextChanged(string key, string value)
        {
            Action<string, string> handler = ContextChanged;
            if (handler != null) handler(key, value);
        }

        internal void RaiseAliasChanged(string alias)
        {
            Action<string> handler = AliasChanged;
            if (handler != null) handler(alias);
        }
    }
}
