// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace EventPipeStress
{
    internal static class Orchestrator
    {
        // TODO: Collect CPU % of reader and writer while running test and add to stats
        // TODO: Standardize and clean up logging from orchestrator and corescaletest
        // TODO: Improve error handling

        internal sealed record OrchestratorArgs(
            CancellationToken Ct,
            FileInfo StressPath,
            int EventSize,
            int EventRate,
            BurstPattern BurstPattern,
            ReaderType ReaderType,
            int SlowReader,
            int Duration,
            int Cores,
            int Threads,
            int EventCount,
            bool Rundown,
            int BufferSize,
            int Iterations,
            bool Pause,
            int EventWaitTimeoutMs,
            Mode Mode);

        public static Task<int> Main(string[] args)
        {
            RootCommand rootCommand = new("EventPipe Stress Tester - Orchestrator")
            {
                // Event generation options
                StressPathArgument,
                ModeOption,
                CommonOptions.EventSizeOption,
                CommonOptions.EventRateOption,
                CommonOptions.BurstPatternOption,
                CommonOptions.DurationOption,
                CoresOption,
                CommonOptions.ThreadsOption,
                CommonOptions.EventCountOption,
                RundownOption,
                BufferSizeOption,
                // Event Reader options
                ReaderTypeOption,
                SlowReaderOption,
                // Orchestrator options
                IterationsOption,
                PauseOption,
                EventWaitTimeoutMsOption,
            };

            rootCommand.SetAction((parseResult, ct) =>
            {
                return Orchestrate(new OrchestratorArgs(
                    Ct: ct,
                    StressPath: parseResult.GetValue(StressPathArgument),
                    EventSize: parseResult.GetValue(CommonOptions.EventSizeOption),
                    EventRate: parseResult.GetValue(CommonOptions.EventRateOption),
                    BurstPattern: parseResult.GetValue(CommonOptions.BurstPatternOption),
                    Duration: parseResult.GetValue(CommonOptions.DurationOption),
                    Cores: parseResult.GetValue(CoresOption),
                    Threads: parseResult.GetValue(CommonOptions.ThreadsOption),
                    EventCount: parseResult.GetValue(CommonOptions.EventCountOption),
                    Rundown: parseResult.GetValue(RundownOption),
                    BufferSize: parseResult.GetValue(BufferSizeOption),
                    ReaderType: parseResult.GetValue(ReaderTypeOption),
                    SlowReader: parseResult.GetValue(SlowReaderOption),
                    Iterations: parseResult.GetValue(IterationsOption),
                    Pause: parseResult.GetValue(PauseOption),
                    EventWaitTimeoutMs: parseResult.GetValue(EventWaitTimeoutMsOption),
                    Mode: parseResult.GetValue(ModeOption)));
            });

            ParseResult parseResult = rootCommand.Parse(args);
            return parseResult.InvokeAsync();
        }

        /// <summary>
        /// This uses CopyTo to copy the trace into a filesystem first, and then uses EventPipeEventSource
        /// on the file to post-process it and return the total # of events read.
        /// </summary>
        private static TestResult UseFS(EventPipeSession session)
        {
            int eventsRead = 0;
            Stopwatch totalTimeSw = new();
            string fileName = Path.Combine(Path.GetTempPath(), "EventPipeStress_" + Guid.NewGuid().ToString("N") + ".nettrace");

            using (FileStream fs = new(fileName, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                totalTimeSw.Start();
                session.EventStream.CopyTo(fs);
                totalTimeSw.Stop();
            }

            using EventPipeEventSource epes = new(fileName);
            epes.Dynamic.All += (TraceEvent data) =>
            {
                eventsRead += 1;
            };

            epes.Process();
            Console.WriteLine($"Read total: {eventsRead}");
            long eventsLost = unchecked((uint)epes.EventsLost);

            // EventsLost is effectively a 32-bit counter; if we observe a wrapped value, pick the smaller
            // interpretation between the raw value and its wrap-around complement.
            const long WrapAround = 1L << 32;
            long wrappedEventsLost = WrapAround - eventsLost;
            if (wrappedEventsLost < eventsLost)
            {
                eventsLost = wrappedEventsLost;
            }

            Console.WriteLine($"Dropped total: {eventsLost}");

            File.Delete(fileName);

            return new(eventsRead, eventsLost, totalTimeSw.Elapsed);
        }

        /// <summary>
        /// This uses EventPipeEventSource's Stream constructor to parse the events real-time.
        /// It then returns the number of events read.
        /// </summary>
        private static TestResult UseEPES(EventPipeSession session, int slowReader)
        {
            int eventsRead = 0;
            Stopwatch slowReadSw = new();
            Stopwatch totalTimeSw = new();
            TimeSpan interval = TimeSpan.FromSeconds(0.75);

            using EventPipeEventSource epes = new(session.EventStream);
            epes.Dynamic.All += (TraceEvent data) =>
            {
                if (!string.Equals(data.ProviderName, "MySource", StringComparison.Ordinal))
                {
                    return;
                }

                eventsRead += 1;
                if (slowReader > 0)
                {
                    if (slowReadSw.Elapsed > interval)
                    {
                        Thread.Sleep(slowReader);
                        slowReadSw.Reset();
                    }
                }
            };

            if (slowReader > 0)
            {
                slowReadSw.Start();
            }

            totalTimeSw.Start();
            epes.Process();
            totalTimeSw.Stop();

            if (slowReader > 0)
            {
                slowReadSw.Stop();
            }

            Console.WriteLine($"Read total: {eventsRead}");
            long eventsLost = unchecked((uint)epes.EventsLost);
            Console.WriteLine($"Dropped total: {eventsLost}");

            return new(eventsRead, eventsLost, totalTimeSw.Elapsed);
        }

        [SupportedOSPlatformGuard("Windows")]
        [SupportedOSPlatformGuard("Linux")]
        private static bool IsWindowsOrLinux => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

        private static async Task<int> Orchestrate(OrchestratorArgs args)
        {
            if (!args.StressPath.Exists)
            {
                Console.Error.WriteLine($"Error: Stress executable not found at {args.StressPath.FullName}");
                return 1;
            }

            if (args.EventWaitTimeoutMs > 0 && args.Mode != Mode.NativeInProc)
            {
                Console.WriteLine("Note: --event-wait-timeout only applies to --mode NativeInProc. Ignoring.");
            }

            if (args.EventRate == -1 && args.BurstPattern != BurstPattern.NONE)
            {
                Console.Error.WriteLine("Must have burst pattern of NONE if rate is -1");
                return 1;
            }

            Func<EventPipeSession, TestResult> readerProc = args.ReaderType switch
            {
                ReaderType.Stream => UseFS,
                ReaderType.EventPipeEventSource => (EventPipeSession session) => UseEPES(session, args.SlowReader),
                _ => throw new ArgumentException("Invalid reader type")
            };

            Console.WriteLine($"Configuration: mode={args.Mode}, event_size={args.EventSize}, event_rate={args.EventRate}, cores={args.Cores}, num_threads={args.Threads}, reader={args.ReaderType}, event_rate_total={(args.EventRate == -1 ? -1 : args.EventRate * args.Threads)}, burst_pattern={args.BurstPattern}, slow_reader={args.SlowReader}, duration={args.Duration}, event_wait_timeout_ms={args.EventWaitTimeoutMs}");

            ProcessStartInfo eventWritingProcStartInfo = args.Mode == Mode.NativeInProc
                ? CreateNativeInProcStressStartInfo(args)
                : CreateStreamingStressStartInfo(args);

            int summaryEventSize = args.Mode == Mode.NativeInProc ? 0 : args.EventSize;
            bool includeDroppedStats = args.Mode != Mode.NativeInProc || args.EventCount > -1;
            TestResults testResults = new(summaryEventSize, includeDroppedStats);
            Console.WriteLine($"Running {args.Iterations} iterations of {eventWritingProcStartInfo.FileName} {eventWritingProcStartInfo.Arguments}");
            for (int iteration = 0; iteration < args.Iterations; iteration++)
            {
                Console.WriteLine("========================================================");
                Console.WriteLine($"Starting iteration {iteration + 1}");

                using Process eventWritingProc = Process.Start(eventWritingProcStartInfo);
                if (eventWritingProc is null)
                {
                    Console.Error.WriteLine("Failed to start Stress process.");
                    continue;
                }

                TaskCompletionSource<bool> doneWritingTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                NativeInProcTelemetry nativeInProcTelemetry = new();
                Task outputReaderTask = ConsumeOutputAsync(eventWritingProc, doneWritingTcs, nativeInProcTelemetry);
                Task errorReaderTask = ConsumeErrorAsync(eventWritingProc);

                if (IsWindowsOrLinux)
                {
                    // Set affinity and priority
                    ulong affinityMask = 0;
                    for (int j = 0; j < args.Cores; j++)
                    {
                        affinityMask |= ((ulong)1 << j);
                    }
                    eventWritingProc.ProcessorAffinity = (IntPtr)((ulong)eventWritingProc.ProcessorAffinity & affinityMask);
                }

                if (args.Pause)
                {
                    Console.WriteLine("Press <enter> to start test");
                    Console.ReadLine();
                }

                if (args.Mode == Mode.NativeInProc)
                {
                    long expectedEvents = 0;
                    if (args.EventCount > -1)
                    {
                        expectedEvents = (long)args.Threads * args.EventCount;
                    }

                    TestResult nativeResult = await RunNativeInProcAsync(eventWritingProc, outputReaderTask, errorReaderTask, nativeInProcTelemetry, expectedEvents).ConfigureAwait(false);
                    testResults.Add(nativeResult);
                }
                else
                {
                    DiagnosticsClient client = new(eventWritingProc.Id);

                    EventPipeSession session;
                    try
                    {
                        session = await StartSessionWithTimeoutAsync(client, args).ConfigureAwait(false);
                    }
                    catch (TimeoutException ex)
                    {
                        Console.Error.WriteLine($"Failed to start EventPipe session for iteration {iteration + 1}: {ex.Message}");

                        // Don't leave the child blocked on its start gate.
                        if (!eventWritingProc.HasExited)
                        {
                            try
                            {
                                StreamWriter writer = eventWritingProc.StandardInput;
                                writer.WriteLine();
                                writer.Flush();
                            }
                            catch (ObjectDisposedException) { }
                            catch (IOException) { }
                        }

                        eventWritingProc.WaitForExit();
                        continue;
                    }

                    using (session)
                    {
                        Console.WriteLine("Session created.");

                        Task<TestResult> listenerTask = Task.Run(() => readerProc(session), CancellationToken.None);

                        // Release the Stress process start gate only after the session is ready,
                        // so we don't miss most of the events for small event-count runs.
                        if (!eventWritingProc.HasExited)
                        {
                            StreamWriter writer = eventWritingProc.StandardInput;
                            writer.WriteLine();
                            writer.Flush();
                        }

                        // Wait until the target reports it's done emitting, then stop the session while the
                        // diagnostics connection is still alive so the runtime can flush remaining buffers.
                        Task doneOrExitTask = await Task.WhenAny(doneWritingTcs.Task, eventWritingProc.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
                        if (doneOrExitTask == doneWritingTcs.Task)
                        {
                            Console.WriteLine("Stress signaled done-writing; stopping session to drain.");
                        }
                        else
                        {
                            Console.WriteLine("Stress exited before done-writing marker; stopping session best-effort.");
                        }

                        session.Stop();

                        TestResult result = await listenerTask.ConfigureAwait(false);
                        testResults.Add(result);

                        // Allow the target to exit after the reader has drained.
                        if (!eventWritingProc.HasExited)
                        {
                            StreamWriter writer = eventWritingProc.StandardInput;
                            writer.WriteLine();
                            writer.Flush();
                        }

                        await eventWritingProc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

                        // Ensure output drains so the child can't block on full pipes.
                        await Task.WhenAll(outputReaderTask, errorReaderTask).ConfigureAwait(false);
                    }
                }

                Console.WriteLine($"Done with iteration {iteration + 1}");
                Console.WriteLine("========================================================");
            }

            Console.WriteLine(testResults.GenerateSummary());
            Console.WriteLine(testResults.GenerateStatisticsTable());

            return 0;
        }

        private static async Task<TestResult> RunNativeInProcAsync(
            Process eventWritingProc,
            Task outputReaderTask,
            Task errorReaderTask,
            NativeInProcTelemetry telemetry,
            long expectedEvents)
        {
            await eventWritingProc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(outputReaderTask, errorReaderTask).ConfigureAwait(false);

            long eventsRead = telemetry.EventsReceived;
            long eventsDropped = expectedEvents > 0 ? Math.Max(0, expectedEvents - eventsRead) : 0;
            TimeSpan duration = telemetry.ElapsedMs > 0 ? TimeSpan.FromMilliseconds(telemetry.ElapsedMs) : TimeSpan.Zero;
            Console.WriteLine($"NativeInProc listener read total: {eventsRead}");
            if (expectedEvents > 0)
            {
                Console.WriteLine($"NativeInProc expected emitted total: {expectedEvents}");
                Console.WriteLine($"NativeInProc inferred dropped: {eventsDropped}");
            }
            if (duration != TimeSpan.Zero)
            {
                Console.WriteLine($"NativeInProc elapsed: {duration.TotalSeconds:N6} seconds");
            }

            return new TestResult(eventsRead, eventsDropped, duration);
        }

        private static async Task ConsumeOutputAsync(Process process, TaskCompletionSource<bool> doneWritingTcs, NativeInProcTelemetry nativeInProcTelemetry)
        {
            while (true)
            {
                string line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                // Forward child stdout for visibility (Stress output is low-volume and useful for diagnosing runs).
                Console.WriteLine(line);

                if (string.Equals(line, "__EVENTPIPESTRESS_DONE__", StringComparison.Ordinal))
                {
                    doneWritingTcs.TrySetResult(true);
                }
                else
                {
                    nativeInProcTelemetry.TrySetFromLine(line);
                }
            }
        }

        private static async Task ConsumeErrorAsync(Process process)
        {
            while (true)
            {
                string line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                // Surface child stderr as orchestrator stderr.
                Console.Error.WriteLine(line);
            }
        }

        private static async Task<EventPipeSession> StartSessionWithTimeoutAsync(DiagnosticsClient client, OrchestratorArgs args)
        {
            // Minimal hang-avoidance:
            // - If the runtime's IPC endpoint isn't ready yet: retry for a short window.
            // - If StartEventPipeSession itself hangs: time out and fail the iteration instead of wedging forever.
            Stopwatch startSessionSw = Stopwatch.StartNew();

            while (true)
            {
                args.Ct.ThrowIfCancellationRequested();

                try
                {
                    Task<EventPipeSession> startTask = Task.Run(() => client.StartEventPipeSession(
                        new EventPipeProvider("MySource", EventLevel.Verbose),
                        args.Rundown,
                        args.BufferSize));

                    return await startTask.WaitAsync(TimeSpan.FromSeconds(5), args.Ct).ConfigureAwait(false);
                }
                catch (ServerNotAvailableException)
                {
                    if (startSessionSw.Elapsed > TimeSpan.FromSeconds(5))
                    {
                        throw new TimeoutException("Timed out waiting for the target diagnostics IPC endpoint.");
                    }

                    await Task.Delay(50, args.Ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException("Timed out starting EventPipe session (StartEventPipeSession did not return).");
                }
            }
        }

        private static readonly Argument<FileInfo> StressPathArgument =
            new Argument<FileInfo>(name: "stress-path")
            {
                Description = "The location of the Stress executable.",
            }.AcceptExistingOnly();

        private static readonly Option<ReaderType> ReaderTypeOption =
            new("--reader-type")
            {
                Description = "The method to read the stream of events.",
                DefaultValueFactory = _ => ReaderType.Stream,
            };

        private static readonly Option<int> SlowReaderOption =
            new("--slow-reader")
            {
                Description = "<Only valid for EventPipeEventSource reader> Delay every read by this many milliseconds.",
                DefaultValueFactory = _ => 0,
            };

        private static readonly Option<int> CoresOption =
            new("--cores")
            {
                Description = "The number of logical cores to restrict the writing process to.",
                DefaultValueFactory = _ => Environment.ProcessorCount,
            };

        private static readonly Option<bool> RundownOption =
            new("--rundown")
            {
                Description = "Should the EventPipe session request rundown events?",
                DefaultValueFactory = _ => true,
            };

        private static readonly Option<int> BufferSizeOption =
            new("--buffer-size")
            {
                Description = "The size of the buffer requested in the EventPipe session",
                DefaultValueFactory = _ => 256,
            };

        private static readonly Option<int> IterationsOption =
            new("--iterations")
            {
                Description = "The number of times to run the test.",
                DefaultValueFactory = _ => 1,
            };

        private static readonly Option<bool> PauseOption =
            new("--pause")
            {
                Description = "Should the orchestrator pause before starting each test phase for a debugger to attach?",
                DefaultValueFactory = _ => false,
            };

        private static readonly Option<Mode> ModeOption =
            new("--mode")
            {
                Description = "How to run: Streaming (default) collects MySource via out-of-proc EventPipeSession; NativeInProc runs Stress in native in-proc listener mode and parses its telemetry.",
                DefaultValueFactory = _ => Mode.Streaming,
            };

        private static readonly Option<int> EventWaitTimeoutMsOption =
            new("--event-wait-timeout")
            {
                Description = "(NativeInProc only) Wait this many milliseconds since the last received exception event before Stress exits.",
                DefaultValueFactory = _ => 0,
            };

        public enum ReaderType
        {
            Stream,
            EventPipeEventSource
        }

        public enum Mode
        {
            Streaming,
            NativeInProc,
        }

        private sealed class NativeInProcTelemetry
        {
            private long _eventsReceived;
            private long _elapsedMs;

            public long EventsReceived => Interlocked.Read(ref _eventsReceived);

            public long ElapsedMs => Interlocked.Read(ref _elapsedMs);

            public void TrySetFromLine(string line)
            {
                const string ReceivedPrefix = "NATIVE_INPROC_RECEIVED=";
                const string ElapsedPrefix = "NATIVE_INPROC_ELAPSED_MS=";

                if (line.StartsWith(ReceivedPrefix, StringComparison.Ordinal))
                {
                    string value = line.Substring(ReceivedPrefix.Length);
                    if (long.TryParse(value, out long parsed))
                    {
                        Interlocked.Exchange(ref _eventsReceived, parsed);
                    }
                }
                else if (line.StartsWith(ElapsedPrefix, StringComparison.Ordinal))
                {
                    string value = line.Substring(ElapsedPrefix.Length);
                    if (long.TryParse(value, out long parsed))
                    {
                        Interlocked.Exchange(ref _elapsedMs, parsed);
                    }
                }
            }
        }

        private static ProcessStartInfo CreateStreamingStressStartInfo(OrchestratorArgs args)
        {
            return new ProcessStartInfo
            {
                FileName = args.StressPath.FullName,
                // Streaming mode: use start/exit gates so we don't miss the beginning and can stop/drain
                // the session before the target tears down its diagnostics connection.
                Arguments = $"--mode Streaming --threads {args.Threads} --event-count {args.EventCount} --event-size {args.EventSize} --event-rate {args.EventRate} --burst-pattern {args.BurstPattern} --duration {args.Duration} --start-gate true --exit-gate true",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }

        private static ProcessStartInfo CreateNativeInProcStressStartInfo(OrchestratorArgs args)
        {
            StringBuilder sb = new();
            sb.Append("--mode NativeInProc");
            sb.Append($" --threads {args.Threads}");
            sb.Append($" --event-count {args.EventCount}");
            sb.Append($" --event-rate {args.EventRate}");
            sb.Append($" --burst-pattern {args.BurstPattern}");
            sb.Append($" --duration {args.Duration}");

            if (args.SlowReader > 0)
            {
                sb.Append($" --slow-reader {args.SlowReader}");
            }

            if (args.EventWaitTimeoutMs > 0)
            {
                sb.Append($" --event-wait-timeout {args.EventWaitTimeoutMs}");
            }

            return new ProcessStartInfo
            {
                FileName = args.StressPath.FullName,
                Arguments = sb.ToString(),
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }
    }
}
