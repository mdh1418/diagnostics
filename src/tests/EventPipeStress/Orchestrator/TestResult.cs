// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EventPipeStress
{
    public record TestResult(long EventsRead, long EventsDropped, TimeSpan Duration)
    {
        public double ThroughputEfficiency => 100 * ((double)EventsRead / ((double)TotalEvents));
        public double EventThroughput => (double)EventsRead / Duration.TotalSeconds;
        public long TotalEvents => EventsRead + EventsDropped;
    }

    public record Stats(string Name, double Min, double Max, double Avg, double Std, string Units = null, int Resolution = 2)
    {
        public string FormattedName => $"{Name}{(Units != null ? $" ({Units})" : "")}";
        public string TableRow => $"|{" " + FormattedName,-35}|{Min.ToString($"N{Resolution}"),20}|{Max.ToString($"N{Resolution}"),20}|{Avg.ToString($"N{Resolution}"),20}|{Std.ToString($"N{Resolution}"),20}|";
        public static string Separator => $"|{new string('-', 35),-35}|{new string('-', 20),-20}|{new string('-', 20),-20}|{new string('-', 20),-20}|{new string('-', 20),-20}|";
        public static string Header => $"|{" stat",-35}|{" Min",-20}|{" Max",-20}|{" Average",-20}|{" Standard Deviation",-20}|";
    }

    public record StatGenerator(string Name, Func<TestResult, double> Calculation, string Units = null, int Resolution = 2)
    {
        public Stats GetStats(IEnumerable<TestResult> results) => results.GetStats(Calculation, Name, Units, Resolution);
    }

    public class TestResults : IEnumerable<TestResult>
    {
        private readonly List<TestResult> _results = new();
        private readonly int _eventSize;
        private readonly bool _includeDroppedStats;

        public TestResults(int eventSize, bool includeDroppedStats = true)
        {
            _eventSize = eventSize;
            _includeDroppedStats = includeDroppedStats;
        }

        public void Add(TestResult result) =>
            _results.Add(result);

        public string GenerateSummary()
        {
            StringBuilder sb = new();
            sb.AppendLine("**** Summary ****");
            int i = 0;
            foreach (TestResult result in _results)
            {
                if (_includeDroppedStats)
                {
                    sb.AppendLine($"iteration {i++ + 1}: {result.EventsRead:N2} events collected, {result.EventsDropped:N2} events dropped in {result.Duration.TotalSeconds:N6} seconds - ({100 * ((double)result.EventsRead / (double)((long)result.EventsRead + result.EventsDropped)):N2}% throughput)");
                }
                else
                {
                    sb.AppendLine($"iteration {i++ + 1}: {result.EventsRead:N2} events collected in {result.Duration.TotalSeconds:N6} seconds");
                }
                if (_eventSize > 0)
                {
                    sb.AppendLine($"\t({(double)result.EventsRead / result.Duration.TotalSeconds:N2} events/s) ({((double)result.EventsRead * _eventSize * sizeof(char)) / result.Duration.TotalSeconds:N2} bytes/s)");
                }
                else
                {
                    sb.AppendLine($"\t({(double)result.EventsRead / result.Duration.TotalSeconds:N2} events/s) (bytes/s N/A)");
                }
            }
            return sb.ToString();
        }

        public string GenerateStatisticsTable()
        {
            StringBuilder sb = new();

            sb.AppendLine();
            sb.AppendLine(Stats.Separator);
            sb.AppendLine(Stats.Header);
            sb.AppendLine(Stats.Separator);
            foreach (StatGenerator generator in StatGenerators)
            {
                sb.AppendLine(generator.GetStats(_results).TableRow);
            }
            sb.AppendLine(Stats.Separator);
            return sb.ToString();
        }

        // Add statistics you want rendered in the results table here
        private IEnumerable<StatGenerator> StatGenerators
        {
            get
            {
                yield return new StatGenerator("Events Read", result => result.EventsRead);
                if (_includeDroppedStats)
                {
                    yield return new StatGenerator("Events Dropped", result => result.EventsDropped);
                    yield return new StatGenerator("Throughput Efficiency", result => result.ThroughputEfficiency, "%");
                }
                yield return new StatGenerator("Event Throughput", result => result.EventThroughput, "events/sec");
                if (_eventSize > 0)
                {
                    yield return new StatGenerator("Data Throughput", result => result.EventThroughput * sizeof(char) * _eventSize, "Bytes/sec");
                }
                yield return new StatGenerator("Duration", result => result.Duration.TotalSeconds, "seconds", 6);
            }
        }

        public IEnumerator<TestResult> GetEnumerator()
        {
            return _results.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _results.GetEnumerator();
        }
    }

    public static class Extensions
    {
        public static Stats GetStats(this IEnumerable<TestResult> results, Func<TestResult, double> calculation, string name, string units = null, int resolution = 2)
        {
            double min = results.Min(calculation);
            double max = results.Max(calculation);
            double avg = results.Average(calculation);
            double std = Math.Sqrt(results.Average(result => Math.Pow(calculation(result) - avg, 2)));

            return new Stats(name, min, max, avg, std, units, resolution);
        }
    }
}
