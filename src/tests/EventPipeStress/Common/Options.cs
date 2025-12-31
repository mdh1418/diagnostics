// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace EventPipeStress
{
    public static class CommonOptions
    {
        public static readonly Option<int> EventSizeOption =
            new("--event-size")
            {
                Description = "The size of the event payload. The payload is a string, so the actual size will be eventSize * sizeof(char) where sizeof(char) is 2 Bytes due to Unicode in C#.",
                DefaultValueFactory = _ => 100,
            };

        public static readonly Option<int> EventRateOption =
            new("--event-rate")
            {
                Description = "The rate of events in events/sec. -1 means 'as fast as possible'.",
                DefaultValueFactory = _ => -1,
            };

        public static readonly Option<BurstPattern> BurstPatternOption =
            new("--burst-pattern")
            {
                Description = "The burst pattern to send events in.",
                DefaultValueFactory = _ => BurstPattern.NONE,
            };

        public static readonly Option<int> DurationOption =
            new("--duration")
            {
                Description = "The number of seconds to send events for.",
                DefaultValueFactory = _ => 60,
            };

        public static readonly Option<int> ThreadsOption =
            new("--threads")
            {
                Description = "The number of threads writing events.",
                DefaultValueFactory = _ => 1,
            };

        public static readonly Option<int> EventCountOption =
            new("--event-count")
            {
                Description = "The total number of events to write per thread. -1 means no limit.",
                DefaultValueFactory = _ => -1,
            };
    }
}
