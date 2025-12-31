# EventPipe Stress

You can use `Orchestrator` and `Stress` to run several stress test scenarios for EventPipe.

These tools are meant for developers working on EventPipe in dotnet/runtime.

## Orchestrator

Help text:

```
$ Orchestrator -h
Orchestrator:
  EventPipe Stress Tester - Orchestrator

Usage:
  Orchestrator [options] <stress-path>

Arguments:
  <stress-path>    The location of the Stress executable.

Options:
  --event-size <event-size>                       The size of the MySource event payload. The payload is a string, so the actual size will be eventSize * sizeof(char) where sizeof(char) is 2 Bytes due to Unicode in C#. For NativeInProc (runtime events), this size/bytes-per-sec metric is not meaningful. [default: 100]
  --event-rate <event-rate>                       The rate of events in events/sec. -1 means 'as fast as possible'. [default: -1]
  --burst-pattern <BOLUS|DRIP|HEAVY_DRIP|NONE>    The burst pattern to send events in. [default: NONE]
  --reader-type <EventPipeEventSource|Stream>     The method to read the stream of events. [default: Stream]
  --slow-reader <slow-reader>                     (Streaming + EventPipeEventSource reader) Delay reads by this many milliseconds. (NativeInProc) Forwarded to Stress as a per-event delay in the in-proc listener. [default: 0]
  --duration <duration>                           The number of seconds to send events for. [default: 60]
  --cores <cores>                                 The number of logical cores to restrict the writing process to. [default: 8]
  --threads <threads>                             The number of threads writing events. [default: 1]
  --event-count <event-count>                     The total number of events to write per thread. -1 means no limit [default: -1]
  --rundown                                       Should the EventPipe session request rundown events? [default: True]
  --buffer-size <buffer-size>                     The size of the buffer requested in the EventPipe session [default: 256]
  --iterations <iterations>                       The number of times to run the test. [default: 1]
  --pause                                         Should the orchestrator pause before starting each test phase for a debugger to attach? [default: False]
  --mode <Streaming|NativeInProc>                  How to run: Streaming (default) collects MySource via out-of-proc EventPipeSession; NativeInProc runs Stress in native in-proc listener mode and parses its telemetry. [default: Streaming]
  --event-wait-timeout <event-wait-timeout>        (NativeInProc only) Wait this many milliseconds since the last received exception event before Stress exits. [default: 0]
  --version                                       Show version information
  -?, -h, --help                                  Show help and usage information
```

## Stress

Help text:

```
$ Stress -h
Stress:
  EventPipe Stress Tester - Stress

Usage:
  Stress [options]

Options:
  --mode <Streaming|NativeInProc>                 How Stress runs: Streaming emits custom MySource events for out-of-proc EventPipe collection; NativeInProc enables an in-proc EventListener for runtime exception events and emits exceptions. [default: Streaming]
  --event-size <event-size>                       The size of the event payload. The payload is a string, so the actual size will be eventSize * sizeof(char) where sizeof(char) is 2 Bytes due to Unicode in C#. [default: 100]
  --event-rate <event-rate>                       The rate of events in events/sec. -1 means 'as fast as possible'. [default: -1]
  --burst-pattern <BOLUS|DRIP|HEAVY_DRIP|NONE>    The burst pattern to send events in. [default: NONE]
  --duration <duration>                           The number of seconds to send events for. [default: 60]
  --threads <threads>                             The number of threads writing events. [default: 1]
  --event-count <event-count>                     The total number of events to write per thread. -1 means no limit [default: -1]
  --slow-reader <slow-reader>                     (NativeInProc mode only) If >0, sleep this many milliseconds per received exception event in OnEventWritten. [default: 0]
  --event-wait-timeout <event-wait-timeout>       (NativeInProc mode only) If >0, wait this many milliseconds since the last received exception event before exiting. If 0, a safe default based on --slow-reader is used. [default: 0]
  --version                                       Show version information
  -?, -h, --help                                  Show help and usage information
```

## Usage

### Prerequisites

1. Build `Orchestrator`
2. Publish `Stress` as a self contained application (`dotnet publish -r <RID> --self-contained`)
3. (optional) Copy over the runtime bits in the `Stress` publish location to test private runtime builds

### Basic Scenarios

1. Send as many events as possible in `N` seconds

`Orchestrate <path-to-Stress> --mode Streaming --duration <N> --iterations 100`

2. Send `N` events as fast as possible

`Orchestrate <path-to-Stress> --mode Streaming --event-count <N> --iterations 100`

3. Send `N` events in a burst pattern of `M` events/sec

`Orchestrate <path-to-Stress> --mode Streaming --event-count <N> --event-rate <M> --burst-pattern bolus --iterations 100`

### Native in-proc runtime exception scenarios

This repo historically focused on **out-of-proc** collection (IPC streaming `EventPipeSession` started via `DiagnosticsClient`).
For stressing **in-proc** consumption (an `EventListener` inside the target process), use `--mode NativeInProc`.
In this mode Stress enables the runtime provider for exception events and emits exceptions; the Orchestrator parses simple telemetry lines from Stress stdout.

Key behavior difference:

* **Streaming**: the Orchestrator must stop the session *before* the target exits, otherwise the runtime tears down the diagnostics connection and the stream ends abruptly (often losing tail events). The Orchestrator therefore uses a simple handshake:
  * Stress waits on a *start gate* so the Orchestrator can start the session first.
  * Stress writes a *done marker* when it has finished emitting events.
  * The Orchestrator stops the session to drain buffers.
  * Then the Orchestrator releases an *exit gate* so the Stress process can terminate.
* **NativeInProc**: no out-of-proc session is started; Stress runs the runtime-exception workload and then exits after a short "no more events" window. The Orchestrator reads:
  * `NATIVE_INPROC_RECEIVED=<n>`
  * `NATIVE_INPROC_ELAPSED_MS=<ms>`

  For **NativeInProc**:
  * If `--event-count` is set (not `-1`), the Orchestrator reports "dropped" as an inferred value:
    `max(0, (threads * event-count) - received)`.
  * If `--event-count -1` (duration-based), the Orchestrator focuses on throughput and does not attempt to compute dropped.

Example:

* **Native runtime in-proc** (exceptions keyword)

`Orchestrate <path-to-Stress> --mode NativeInProc --threads 1000 --event-count 1000 --iterations 20`

You can optionally add per-event listener work and tune the post-workload wait window:

`Orchestrate <path-to-Stress> --mode NativeInProc --slow-reader 5 --event-wait-timeout 500 --threads 1000 --event-count 1000 --iterations 20`

### Sample Output

```
**** Summary ****
iteration 1: 102,678.00 events collected, 0.00 events dropped in 0.283581 seconds - (100.00% throughput)
        (362,076.44 events/s) (181,038,221.88 bytes/s)
iteration 2: 102,678.00 events collected, 0.00 events dropped in 0.634398 seconds - (100.00% throughput)
        (161,851.05 events/s) (80,925,526.10 bytes/s)
iteration 3: 102,678.00 events collected, 0.00 events dropped in 0.652566 seconds - (100.00% throughput)
        (157,344.96 events/s) (78,672,477.98 bytes/s)
iteration 4: 102,678.00 events collected, 0.00 events dropped in 0.661910 seconds - (100.00% throughput)
        (155,123.83 events/s) (77,561,915.90 bytes/s)
iteration 5: 102,678.00 events collected, 0.00 events dropped in 0.632966 seconds - (100.00% throughput)
        (162,217.35 events/s) (81,108,673.20 bytes/s)


|-----------------------------------|--------------------|--------------------|--------------------|--------------------|
| stat                              | Min                | Max                | Average            | Standard Deviation |
|-----------------------------------|--------------------|--------------------|--------------------|--------------------|
| Events Read                       |          102,678.00|          102,678.00|          102,678.00|                0.00|
| Events Dropped                    |                0.00|                0.00|                0.00|                0.00|
| Throughput Efficiency (%)         |              100.00|              100.00|              100.00|                0.00|
| Event Throughput (events/sec)     |          155,123.83|          362,076.44|          199,722.73|           81,221.41|
| Data Throughput (Bytes/sec)       |       77,561,915.90|      181,038,221.88|       99,861,363.01|       40,610,702.78|
| Duration (seconds)                |            0.283581|            0.661910|            0.573084|            0.145165|
|-----------------------------------|--------------------|--------------------|--------------------|--------------------|
```