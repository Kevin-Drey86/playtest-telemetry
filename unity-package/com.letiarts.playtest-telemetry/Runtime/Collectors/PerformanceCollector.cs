using System;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Records every frame time into a preallocated buffer (no per-frame allocations) and, every sample
    /// interval, emits average FPS, 1% low FPS, max frame time, memory, battery and thermal state.
    /// </summary>
    internal sealed class PerformanceCollector : ITelemetryModule
    {
        const int BufferSize = 16384;   // frames per interval; if exceeded, the oldest frames are overwritten
        const int MinFrames = 10;

        readonly float[] frameTimes = new float[BufferSize];
        readonly float[] scratch = new float[BufferSize];
        readonly IThermalProvider thermal;

        TelemetryContext context;
        int frameCount;      // total frames this interval (may exceed BufferSize)
        float elapsed;
        double mainThreadNsSum;
        double gcBytesSum;
        int recorderFrames;

        ProfilerRecorder mainThreadRecorder;
        ProfilerRecorder gcAllocRecorder;
        ProfilerRecorder usedMemoryRecorder;

        public PerformanceCollector(IThermalProvider thermalProvider)
        {
            thermal = thermalProvider;
        }

        public void Initialize(TelemetryContext ctx)
        {
            context = ctx;
            // Recorders are only valid where the profiler counters exist (Editor / Development builds);
            // elsewhere they stay invalid and we fall back below.
            try
            {
                mainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
                gcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
                usedMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Used Memory", 1);
            }
            catch (Exception e)
            {
                PtLog.Warn("profiler recorders unavailable: " + e.Message);
            }
        }

        public void Tick(float unscaledDt)
        {
            frameTimes[frameCount % BufferSize] = unscaledDt;
            frameCount++;
            elapsed += unscaledDt;

            if (mainThreadRecorder.Valid) mainThreadNsSum += mainThreadRecorder.LastValue;
            if (gcAllocRecorder.Valid) gcBytesSum += gcAllocRecorder.LastValue;
            recorderFrames++;

            if (elapsed >= context.Settings.perfSampleIntervalSeconds) EmitSample();
        }

        void EmitSample()
        {
            int n = Math.Min(frameCount, BufferSize);
            if (n >= MinFrames && context.CanCollect)
            {
                float sum = 0f, max = 0f;
                for (int i = 0; i < n; i++)
                {
                    float t = frameTimes[i];
                    sum += t;
                    if (t > max) max = t;
                    scratch[i] = t;
                }
                double fpsAvg = sum > 0f ? n / (double)sum : 0.0;

                // 1% low: average FPS of the slowest 1% of frames.
                Array.Sort(scratch, 0, n);
                int worst = Math.Max(1, n / 100);
                float worstSum = 0f;
                for (int i = n - worst; i < n; i++) worstSum += scratch[i];
                double fpsP1Low = worstSum > 0f ? worst / (double)worstSum : 0.0;

                double? mainThreadMs = null;
                if (mainThreadRecorder.Valid && recorderFrames > 0) mainThreadMs = mainThreadNsSum / recorderFrames / 1e6;
                double? gcKb = null;
                if (gcAllocRecorder.Valid && recorderFrames > 0) gcKb = gcBytesSum / recorderFrames / 1024.0;

                double? battery = null;
                string batteryStatus = null;
                float level = SystemInfo.batteryLevel;
                if (level >= 0f)
                {
                    battery = level;
                    batteryStatus = SystemInfo.batteryStatus.ToString();
                }

                string thermalState = null;
                try
                {
                    if (thermal != null) thermalState = thermal.GetThermalState();
                }
                catch (Exception)
                {
                    thermalState = null;
                }

                string json = ItemBuilder.PerfSample(
                    Clock.NowMs(), context.CurrentScene, fpsAvg, fpsP1Low, max * 1000.0,
                    mainThreadMs, gcKb, UsedMemoryMb(), battery, batteryStatus, thermalState);
                context.Client.Enqueue(new QueueItem(ItemKind.Perf, ItemPriority.Perf, json));
            }

            frameCount = 0;
            elapsed = 0f;
            mainThreadNsSum = 0;
            gcBytesSum = 0;
            recorderFrames = 0;
        }

        int UsedMemoryMb()
        {
            long bytes = 0;
            if (usedMemoryRecorder.Valid) bytes = usedMemoryRecorder.LastValue;
            if (bytes <= 0) bytes = Profiler.GetTotalAllocatedMemoryLong();
            if (bytes <= 0) bytes = GC.GetTotalMemory(false);
            return (int)(bytes / (1024 * 1024));
        }

        public void Shutdown()
        {
            if (mainThreadRecorder.Valid) mainThreadRecorder.Dispose();
            if (gcAllocRecorder.Valid) gcAllocRecorder.Dispose();
            if (usedMemoryRecorder.Valid) usedMemoryRecorder.Dispose();
        }
    }
}
