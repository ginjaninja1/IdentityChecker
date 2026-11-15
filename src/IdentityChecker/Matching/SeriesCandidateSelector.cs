using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using IdentityHelper.Configuration;

namespace IdentityHelper.Matching
{
    public sealed class SeriesCandidate
    {
        public string Name { get; set; }
        public int? Year { get; set; }
        public string TvdbId { get; set; }
        public string TmdbId { get; set; }
        public string Source { get; set; }
        public int Position { get; set; }
    }

    public sealed class SeriesCandidateScore
    {
        public SeriesCandidate Candidate { get; set; }
        public string NormalizedInput { get; set; }
        public string NormalizedCandidate { get; set; }
        public int EditDistance { get; set; }
        public double TitleSimilarity { get; set; }
        public int? YearDelta { get; set; }
        public double YearScore { get; set; }
        public double TotalScore { get; set; }
    }

    public sealed class SeriesSelection
    {
        public SeriesCandidateScore Selected { get; set; }
        public IReadOnlyList<SeriesCandidateScore> Ranked { get; set; }
        public string Reason { get; set; }
        public double Margin { get; set; }
    }

    public static class SeriesCandidateSelector
    {
        public static SeriesSelection Select(string name, int? year, IEnumerable<SeriesCandidate> candidates,
            PluginConfiguration configuration, IReadOnlyList<string> sourceOrder)
        {
            var list = (candidates ?? Enumerable.Empty<SeriesCandidate>()).ToList();
            var titleWeight = ValidWeight(configuration.TitleWeight, 0.8);
            var yearWeight = ValidWeight(configuration.YearWeight, 0.2);
            var weightTotal = titleWeight + yearWeight;
            if (weightTotal <= 0) { titleWeight = 0.8; yearWeight = 0.2; weightTotal = 1; }
            titleWeight /= weightTotal;
            yearWeight /= weightTotal;
            var input = Normalize(name);
            var currentYear = DateTime.UtcNow.Year;
            var ranked = list.Select(candidate => Score(candidate, input, year, currentYear, titleWeight, yearWeight))
                .OrderByDescending(x => x.TotalScore)
                .ThenBy(x => SourceOrderIndex(sourceOrder, x.Candidate.Source))
                .ThenByDescending(x => x.TitleSimilarity >= 1.0)
                .ThenByDescending(x => x.TitleSimilarity >= 1.0 && !year.HasValue ? x.YearScore : 0)
                .ThenBy(x => year.HasValue ? x.YearDelta ?? int.MaxValue : 0)
                .ThenBy(x => x.Candidate.Position)
                .ToList();
            var margin = ranked.Count > 1 ? ranked[0].TotalScore - ranked[1].TotalScore : (ranked.Count == 1 ? ranked[0].TotalScore : 0);
            string reason;
            SeriesCandidateScore selected = null;
            if (ranked.Count == 0) reason = "No candidates";
            else { selected = ranked[0]; reason = "Selected highest-ranked available candidate"; }
            return new SeriesSelection { Selected = selected, Ranked = ranked, Reason = reason, Margin = margin };
        }

        private static int SourceOrderIndex(IReadOnlyList<string> sourceOrder, string source)
        {
            if (sourceOrder == null) return int.MaxValue;
            for (var i = 0; i < sourceOrder.Count; i++)
                if (string.Equals(sourceOrder[i], source, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }

        private static SeriesCandidateScore Score(SeriesCandidate candidate, string input, int? year, int currentYear, double titleWeight, double yearWeight)
        {
            var normalized = Normalize(candidate.Name);
            var distance = Levenshtein(input, normalized);
            var title = input.Length == 0 && normalized.Length == 0 ? 0 : 1.0 - (double)distance / Math.Max(input.Length, normalized.Length);
            int? delta = year.HasValue && candidate.Year.HasValue ? Math.Abs(year.Value - candidate.Year.Value) : (int?)null;
            // With no folder year, year is only a tie-break for exact title matches.
            // Prefer current/recent series, leave missing years neutral, and reduce
            // the preference smoothly for older series without letting it outweigh title similarity.
            var yearScore = !year.HasValue
                ? candidate.Year.HasValue ? Math.Max(0, 1.0 - Math.Max(0, currentYear - candidate.Year.Value) / 10.0) : 0.5
                : candidate.Year.HasValue ? Math.Max(0, 1.0 - delta.Value / 5.0) : 0;
            return new SeriesCandidateScore { Candidate = candidate, NormalizedInput = input, NormalizedCandidate = normalized,
                EditDistance = distance, TitleSimilarity = title, YearDelta = delta, YearScore = yearScore,
                TotalScore = titleWeight * title + yearWeight * yearScore };
        }

        public static string Normalize(string value)
        {
            var text = (value ?? string.Empty).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            var result = new StringBuilder(text.Length);
            var pendingSpace = false;
            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (pendingSpace && result.Length > 0) result.Append(' ');
                    result.Append(c);
                    pendingSpace = false;
                }
                else pendingSpace = true;
            }
            return result.ToString().Trim();
        }

        private static int Levenshtein(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var previous = Enumerable.Range(0, b.Length + 1).ToArray();
            var current = new int[b.Length + 1];
            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var swap = previous; previous = current; current = swap;
            }
            return previous[b.Length];
        }

        private static double Clamp(double value) => double.IsNaN(value) || double.IsInfinity(value) ? 1 : Math.Max(0, Math.Min(1, value));
        private static double ValidWeight(double value, double fallback) => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? fallback : value;
    }
}
