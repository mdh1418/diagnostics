// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;

namespace EventPipeStress
{
    // Each pattern sends N events per second, but varies the frequency
    // across that second to simulate various burst patterns.
    public enum BurstPattern
    {
        DRIP = 0, // (default) to send N events per second, sleep 1000/N ms between sends
        BOLUS = 1, // each second send N events then stop
        HEAVY_DRIP = 2, // each second send M bursts of N/M events
        NONE = -1, // no burst pattern
    }

    public static class BurstPatternMethods
    {
        public static BurstPattern ToBurstPattern(this int n)
        {
            return n > 2 || n < -1 ? BurstPattern.NONE : (BurstPattern)n;
        }

        public static BurstPattern ToBurstPattern(this string str)
        {
            if (int.TryParse(str, out int result))
            {
                return result.ToBurstPattern();
            }

            return str.ToLowerInvariant() switch
            {
                "drip" => BurstPattern.DRIP,
                "bolus" => BurstPattern.BOLUS,
                "heavy_drip" => BurstPattern.HEAVY_DRIP,
                "none" => BurstPattern.NONE,
                _ => BurstPattern.NONE,
            };
        }

        public static string ToString(this BurstPattern burstPattern) => burstPattern switch
        {
            BurstPattern.DRIP => "DRIP",
            BurstPattern.BOLUS => "BOLUS",
            BurstPattern.HEAVY_DRIP => "HEAVY_DRIP",
            BurstPattern.NONE => "NONE",
            _ => "UNKNOWN",
        };

        public static void DefaultSleepAction(int duration)
        {
            Thread.Sleep(duration);
        }

        public static void BusySleepAction(int duration)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < duration)
            {
                Thread.SpinWait(1000);
            }
        }

        /// <summary>
        /// Invoke <paramref name="method"/> <paramref name="rate"/> times in 1 second using the <paramref name="pattern"/> provided.
        /// </summary>
        public static Func<long> Burst(BurstPattern pattern, int rate, Action method, Action<int> sleepAction)
        {
            if (rate == 0)
            {
                throw new ArgumentException("Rate cannot be 0", nameof(rate));
            }

            if (sleepAction is null)
            {
                throw new ArgumentNullException(nameof(sleepAction));
            }

            switch (pattern)
            {
                case BurstPattern.DRIP:
                {
                    int sleepInMs = (int)Math.Floor(1000.0 / rate);
                    return () =>
                    {
                        method();
                        sleepAction(sleepInMs);
                        return 1;
                    };
                }
                case BurstPattern.BOLUS:
                {
                    return () =>
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        for (int i = 0; i < rate; i++)
                        {
                            method();
                        }

                        long remainingMs = 1000 - sw.ElapsedMilliseconds;
                        if (remainingMs > 0)
                        {
                            sleepAction((int)remainingMs);
                        }

                        return rate;
                    };
                }
                case BurstPattern.HEAVY_DRIP:
                {
                    const int nDrips = 4;
                    int nEventsPerDrip = (int)Math.Floor((double)rate / nDrips);
                    int sleepInMs = (int)Math.Floor((1000.0 / rate) / nDrips);
                    return () =>
                    {
                        for (int i = 0; i < nDrips; i++)
                        {
                            for (int j = 0; j < nEventsPerDrip; j++)
                            {
                                method();
                            }

                            sleepAction(sleepInMs);
                        }

                        return nEventsPerDrip * nDrips;
                    };
                }
                case BurstPattern.NONE:
                {
                    return () =>
                    {
                        method();
                        return 1;
                    };
                }
                default:
                    throw new ArgumentException("Unknown burst pattern", nameof(pattern));
            }
        }
    }
}
