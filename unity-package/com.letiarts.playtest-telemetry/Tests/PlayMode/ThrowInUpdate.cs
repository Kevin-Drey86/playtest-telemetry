using System;
using UnityEngine;

namespace LetiArts.PlaytestTelemetry.Tests
{
    /// <summary>Throws from Update a fixed number of times.</summary>
    public sealed class ThrowInUpdate : MonoBehaviour
    {
        public int Remaining = 5;

        void Update()
        {
            if (Remaining <= 0) return;
            Remaining--;
            throw new InvalidOperationException("update failed");
        }
    }
}
