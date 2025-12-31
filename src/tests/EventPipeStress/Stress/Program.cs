// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EventPipeStress
{
    internal static partial class Stress
    {
        private enum Mode
        {
            Streaming,
            NativeInProc
        }

        public static Task<int> Main(string[] args)
        {
            RootCommand rootCommand = new("EventPipe Stress Tester - Stress")
            {
                ModeOption,

                // Common event emission options
                CommonOptions.EventSizeOption,
                CommonOptions.EventRateOption,
                CommonOptions.BurstPatternOption,
                CommonOptions.DurationOption,
                CommonOptions.ThreadsOption,
                CommonOptions.EventCountOption,

                // Process control (used by Orchestrator for out-of-proc streaming mode)
                StartGateOption,
                ExitGateOption,

                // Native in-proc listener tuning
                SlowReaderMsOption,
                EventWaitTimeoutMsOption,
            };

            rootCommand.SetAction((parseResult, ct) => Run(parseResult, ct));

            ParseResult parseResult = rootCommand.Parse(args);
            return parseResult.InvokeAsync();
        }

        private static async Task<int> Run(ParseResult parseResult, CancellationToken ct)
        {
            if (Console.Out is StreamWriter outWriter)
            {
                outWriter.AutoFlush = true;
            }
            if (Console.Error is StreamWriter errWriter)
            {
                errWriter.AutoFlush = true;
            }

            Mode mode = parseResult.GetValue(ModeOption);

            int eventSize = parseResult.GetValue(CommonOptions.EventSizeOption);
            int eventRate = parseResult.GetValue(CommonOptions.EventRateOption);
            BurstPattern burstPattern = parseResult.GetValue(CommonOptions.BurstPatternOption);
            int threads = parseResult.GetValue(CommonOptions.ThreadsOption);
            int duration = parseResult.GetValue(CommonOptions.DurationOption);
            int eventCount = parseResult.GetValue(CommonOptions.EventCountOption);

            TimeSpan durationTimeSpan = TimeSpan.FromSeconds(duration);

            if (mode == Mode.NativeInProc)
            {
                int slowReaderMs = parseResult.GetValue(SlowReaderMsOption);
                int eventWaitTimeoutMs = parseResult.GetValue(EventWaitTimeoutMsOption);

                return await RunNativeInProcAsync(
                    burstPattern,
                    eventRate,
                    threads,
                    durationTimeSpan,
                    eventCount,
                    slowReaderMs,
                    eventWaitTimeoutMs,
                    ct).ConfigureAwait(false);
            }

            bool startGate = parseResult.GetValue(StartGateOption);
            bool exitGate = parseResult.GetValue(ExitGateOption);

            return await RunStreamingAsync(
                burstPattern,
                eventRate,
                threads,
                durationTimeSpan,
                eventCount,
                eventSize,
                startGate,
                exitGate,
                ct).ConfigureAwait(false);
        }

        // Streaming mode emits custom events; Orchestrator will collect them out-of-proc via EventPipeSession.
        private static async Task<int> RunStreamingAsync(
            BurstPattern burstPattern,
            int eventRate,
            int threads,
            TimeSpan durationTimeSpan,
            int eventCount,
            int eventSize,
            bool startGate,
            bool exitGate,
            CancellationToken ct)
        {
            if (eventSize != 100)
            {
                MySource.s_Payload = new('a', eventSize);
            }

            bool finished = false;
            Action threadProc = MakeThreadProc(
                burstPattern,
                eventRate,
                eventCount,
                MySource.Log.FireEvent,
                isFinished: () => finished);

            Thread[] threadArray = new Thread[threads];
            TaskCompletionSource<bool>[] tcsArray = new TaskCompletionSource<bool>[threads];

            for (int i = 0; i < threads; i++)
            {
                TaskCompletionSource<bool> tcs = new();
                threadArray[i] = new Thread(() =>
                {
                    threadProc();
                    tcs.TrySetResult(true);
                });
                tcsArray[i] = tcs;
            }

            Console.WriteLine($"SUBPROCESSS :: Streaming - Threads: {threads}, EventSize: {eventSize * sizeof(char):N} bytes, EventCount: {(eventCount == -1 ? -1 : eventCount * threads)}, EventRate: {(eventRate == -1 ? -1 : eventRate * threads)} events/sec, duration: {durationTimeSpan.TotalSeconds}s");

            if (startGate)
            {
                Console.WriteLine("STRESS_WAITING_START_GATE");
                Console.ReadLine();
            }

            for (int i = 0; i < threads; i++)
            {
                threadArray[i].Start();
            }

            Task threadCompletionTask = Task.WhenAll(tcsArray.Select(tcs => tcs.Task));
            await Task.WhenAny(Task.Delay(durationTimeSpan, ct), threadCompletionTask).ConfigureAwait(false);
            finished = true;

            await threadCompletionTask.ConfigureAwait(false);

            Console.WriteLine("__EVENTPIPESTRESS_DONE__");

            if (exitGate)
            {
                Console.WriteLine("STRESS_WAITING_EXIT_GATE");
                Console.ReadLine();
            }

            return 0;
        }

        // Native in-proc mode:
        // - Use an in-proc EventListener tuned to runtime exception events.
        // - Emit exceptions (no custom EventSource).
        // - Exit after the listener has been quiet long enough.
        private static async Task<int> RunNativeInProcAsync(
            BurstPattern burstPattern,
            int eventRate,
            int threads,
            TimeSpan durationTimeSpan,
            int eventCount,
            int slowReaderMs,
            int eventWaitTimeoutMs,
            CancellationToken ct)
        {
            NativeRuntimeExceptionListener listener = new(slowReaderMs);

            if (eventWaitTimeoutMs < slowReaderMs)
            {
                Console.WriteLine($"Cannot have event wait timeout {eventWaitTimeoutMs}ms less than slow reader delay {slowReaderMs}ms.");
                Console.WriteLine($"Adjusting event wait timeout from {eventWaitTimeoutMs}ms to {slowReaderMs + 50}ms to account for slow reader delay.");
                eventWaitTimeoutMs = slowReaderMs + 50;
            }

            bool finished = false;
            Action threadProc = MakeThreadProc(burstPattern, eventRate, eventCount, ThrowAndCatchException, isFinished: () => finished);

            Thread[] threadArray = new Thread[threads];
            TaskCompletionSource<bool>[] tcsArray = new TaskCompletionSource<bool>[threads];

            for (int i = 0; i < threads; i++)
            {
                TaskCompletionSource<bool> tcs = new();
                threadArray[i] = new Thread(() =>
                {
                    threadProc();
                    tcs.TrySetResult(true);
                });
                tcsArray[i] = tcs;
            }

            Console.WriteLine($"SUBPROCESSS :: NativeInProc - Threads: {threads}, EventCount: {(eventCount == -1 ? -1 : eventCount * threads)}, EventRate: {(eventRate == -1 ? -1 : eventRate * threads)} events/sec, duration: {durationTimeSpan.TotalSeconds}s, slowReaderMs={slowReaderMs}");

            Stopwatch emitSw = Stopwatch.StartNew();
            for (int i = 0; i < threads; i++)
            {
                threadArray[i].Start();
            }

            Task threadCompletionTask = Task.WhenAll(tcsArray.Select(tcs => tcs.Task));
            await Task.WhenAny(Task.Delay(durationTimeSpan, ct), threadCompletionTask).ConfigureAwait(false);
            finished = true;

            await threadCompletionTask.ConfigureAwait(false);

            await WaitForEventListenerCompletion(listener, TimeSpan.FromMilliseconds(eventWaitTimeoutMs), ct).ConfigureAwait(false);

            emitSw.Stop();
            Console.WriteLine($"NATIVE_INPROC_RECEIVED={listener.EventsReceived}");
            Console.WriteLine($"NATIVE_INPROC_ELAPSED_MS={emitSw.ElapsedMilliseconds}");

            return 0;
        }

        private static async Task WaitForEventListenerCompletion(
            NativeRuntimeExceptionListener listener,
            TimeSpan quietPeriod,
            CancellationToken ct)
        {
            long lastSeenCount = listener.EventsReceived;
            long lastProgressTimestamp = Stopwatch.GetTimestamp();

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                long currentCount = listener.EventsReceived;
                if (currentCount != lastSeenCount)
                {
                    lastSeenCount = currentCount;
                    lastProgressTimestamp = Stopwatch.GetTimestamp();
                }

                TimeSpan sinceLast = Stopwatch.GetElapsedTime(lastProgressTimestamp);
                if (sinceLast >= quietPeriod)
                {
                    Console.WriteLine($"Event listener has been quiet for {sinceLast.TotalMilliseconds:N0}ms, exceeding quiet period of {quietPeriod.TotalMilliseconds:N0}ms. Exiting.");
                    return;
                }

                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        private static Action MakeThreadProc(
            BurstPattern burstPattern,
            int eventRate,
            int eventCount,
            Action emitAction,
            Func<bool> isFinished)
        {
            Func<long> burst = BurstPatternMethods.Burst(burstPattern, eventRate, emitAction, BurstPatternMethods.BusySleepAction);

            if (eventCount != -1)
            {
                return () =>
                {
                    long messagesSent = 0;
                    while (!isFinished() && messagesSent < eventCount)
                    {
                        messagesSent += burst();
                    }
                };
            }

            return () =>
            {
                while (!isFinished())
                {
                    burst();
                }
            };
        }

        private static void ThrowAndCatchException()
        {
            try
            {
                throw new InvalidOperationException("EventPipeStress exception workload");
            }
            catch {}
        }

        private sealed class NativeRuntimeExceptionListener : EventListener
        {
            private const string RuntimeProviderName = "Microsoft-Windows-DotNETRuntime";
            private const EventKeywords ExceptionKeyword = (EventKeywords)0x8000;

            // Fast-path assumption for this benchmark:
            // - This EventListener only enables the runtime provider above.
            // - EventListener dispatch into OnEventWritten is effectively sequential for this listener.
            // Under those conditions we can skip provider checks and use a non-atomic increment to
            // minimize overhead in the hot path.

            private readonly int _slowReaderMs;
            private EventSource _runtimeEventSource;
            private long _eventsReceived;

            public NativeRuntimeExceptionListener(int slowReaderMs)
            {
                _slowReaderMs = slowReaderMs;
            }

            public long EventsReceived => Volatile.Read(ref _eventsReceived);

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (string.Equals(eventSource.Name, RuntimeProviderName, StringComparison.Ordinal))
                {
                    _runtimeEventSource = eventSource;
                    EnableEvents(eventSource, EventLevel.Informational, ExceptionKeyword);
                }
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                if (eventData.EventId != 80)
                {
                    return;
                }

                _eventsReceived++;

                if (_slowReaderMs > 0)
                {
                    Thread.Sleep(_slowReaderMs);
                }
            }
        }
    }

    internal sealed class MySource : EventSource
    {
        public static MySource Log = new();
        public static string s_Payload = new('a', 100);
        public void FireEvent() => WriteEvent(1, s_Payload);
    }

    internal static partial class Stress
    {
        private static readonly Option<Mode> ModeOption =
            new("--mode")
            {
                Description = "How Stress runs: Streaming emits custom MySource events for out-of-proc EventPipe collection; NativeInProc enables an in-proc EventListener for runtime exception events and emits exceptions.",
                DefaultValueFactory = _ => Mode.Streaming,
            };

        private static readonly Option<bool> StartGateOption =
            new("--start-gate")
            {
                Description = "(Streaming mode only) If true, wait for a line on stdin before starting event emission (used by Orchestrator).",
                DefaultValueFactory = _ => true,
            };

        private static readonly Option<bool> ExitGateOption =
            new("--exit-gate")
            {
                Description = "(Streaming mode only) If true, after finishing event emission write a done marker and wait for a line on stdin before exiting (allows the out-of-proc reader to drain).",
                DefaultValueFactory = _ => false,
            };


        private static readonly Option<int> SlowReaderMsOption =
            new("--slow-reader")
            {
                Description = "(NativeInProc mode only) If >0, sleep this many milliseconds per received exception event in OnEventWritten.",
                DefaultValueFactory = _ => 0,
            };

        private static readonly Option<int> EventWaitTimeoutMsOption =
            new("--event-wait-timeout")
            {
                Description = "(NativeInProc mode only) If >0, wait this many milliseconds since the last received exception event before exiting. If 0, a safe default based on --slow-reader is used.",
                DefaultValueFactory = _ => 250,
            };
    }
}
