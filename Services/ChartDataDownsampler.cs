namespace COMS_MVC.Services
{
    /// <summary>
    /// One chart point. <see cref="IsAggregate"/> is false because every
    /// emitted point is a real stored reading; downsampling selects the
    /// highest and lowest actual water-level readings per time bucket so
    /// peaks and unusual lows are never averaged away or fabricated.
    /// </summary>
    public sealed class ChartReadingPoint
    {
        public DateTime RecordedAt { get; set; }
        public double WaterLevel { get; set; }
        public double FlowRate { get; set; }
        public double DebrisLevel { get; set; }
        public double Turbidity { get; set; }
        public double Temperature { get; set; }
        public bool IsSimulated { get; set; }

        public bool IsAggregate { get; set; }
        public string Aggregation { get; set; } = "actual";
        public DateTime? BucketStartUtc { get; set; }
        public DateTime? BucketEndUtc { get; set; }
    }

    public static class ChartDataDownsampler
    {
        /// <summary>
        /// Reduces a chronologically ordered reading list to at most
        /// <paramref name="maxPoints"/> points. When the input fits, it is
        /// returned unchanged. Otherwise each time bucket contributes its
        /// lowest and highest actual water-level readings (at most two per
        /// bucket), preserving spikes without inventing values.
        /// </summary>
        public static List<ChartReadingPoint> Downsample(
            IReadOnlyList<ChartReadingPoint> readings,
            int maxPoints,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc)
        {
            if (readings.Count == 0)
            {
                return new List<ChartReadingPoint>();
            }

            maxPoints = Math.Clamp(maxPoints, 20, 2000);
            if (readings.Count <= maxPoints)
            {
                return readings.ToList();
            }

            var start = rangeStartUtc;
            var end = rangeEndUtc;
            if (end <= start)
            {
                start = readings[0].RecordedAt;
                end = readings[^1].RecordedAt;
            }
            if (end <= start)
            {
                end = start.AddSeconds(1);
            }

            // Two actual readings (min + max) per bucket.
            var bucketCount = Math.Max(1, maxPoints / 2);
            var totalSeconds = Math.Max(1d, (end - start).TotalSeconds);
            var bucketSeconds = Math.Max(1d, totalSeconds / bucketCount);

            var selected = new List<ChartReadingPoint>(maxPoints);
            foreach (var group in readings
                .GroupBy(r => Math.Clamp(
                    (int)((r.RecordedAt - start).TotalSeconds / bucketSeconds),
                    0, bucketCount - 1))
                .OrderBy(g => g.Key))
            {
                var ordered = group.OrderBy(r => r.RecordedAt).ToList();
                var lowest = ordered.OrderBy(r => r.WaterLevel).ThenBy(r => r.RecordedAt).First();
                var highest = ordered.OrderByDescending(r => r.WaterLevel).ThenBy(r => r.RecordedAt).First();
                var bucketStart = start.AddSeconds(group.Key * bucketSeconds);
                var bucketEnd = bucketStart.AddSeconds(bucketSeconds);

                AddPeak(selected, lowest, bucketStart, bucketEnd);
                if (!ReferenceEquals(lowest, highest))
                {
                    AddPeak(selected, highest, bucketStart, bucketEnd);
                }
            }

            return selected
                .OrderBy(r => r.RecordedAt)
                .Take(maxPoints)
                .ToList();
        }

        private static void AddPeak(
            List<ChartReadingPoint> target,
            ChartReadingPoint source,
            DateTime bucketStart,
            DateTime bucketEnd)
        {
            target.Add(new ChartReadingPoint
            {
                RecordedAt = source.RecordedAt,
                WaterLevel = source.WaterLevel,
                FlowRate = source.FlowRate,
                DebrisLevel = source.DebrisLevel,
                Turbidity = source.Turbidity,
                Temperature = source.Temperature,
                IsSimulated = source.IsSimulated,
                IsAggregate = false,
                Aggregation = "actual-peak",
                BucketStartUtc = bucketStart,
                BucketEndUtc = bucketEnd
            });
        }
    }
}
