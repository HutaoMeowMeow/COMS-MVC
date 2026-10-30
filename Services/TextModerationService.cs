using Microsoft.Extensions.Options;
using System.Text;

namespace COMS_MVC.Services
{
    /// <summary>
    /// Server-side textual moderation for community report titles and descriptions.
    ///
    /// Design principles:
    ///  * Never the sole decision mechanism: repetition / keyboard-mashing /
    ///    gibberish / symbol heuristics are combined with a small, configurable
    ///    blocklist of clearly-targeted profanity and explicitly sexual terms.
    ///  * Context-aware: sanitation-related terms (human waste, sewage, toilet,
    ///    septage, drain, canal, flood, garbage, debris) are treated as
    ///    legitimate report vocabulary, so a blocklist hit inside a sanitation
    ///    context does not auto-reject the report.
    ///  * Meaningfulness: a description must communicate a real canal,
    ///    drainage, or flood-related issue. Gibberish, keyboard mashing, and
    ///    repeated meaningless words are rejected even when they are long
    ///    enough to pass length validation. Short but meaningful descriptions
    ///    are accepted.
    ///  * Multilingual: English, Cebuano, and Filipino report vocabulary is
    ///    recognized, including minor spelling errors, so legitimate local
    ///    language reports are not rejected.
    ///  * Uncertainty: when the system cannot confidently determine whether a
    ///    description is meaningful or COMS-related, it is classified as
    ///    Uncertain (staff review) rather than silently accepted or rejected.
    ///  * Configurable: blocklists and thresholds come from
    ///    appsettings.json ("TextModeration"); sensible defaults ship in code
    ///    so the feature works without any external service or API key.
    ///  * No external dependency: no third-party moderation service is called.
    ///    If an external service is later desired, replace this implementation
    ///    behind ITextModerationService.
    /// </summary>
    public sealed class TextModerationOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Clearly-targeted profanity / abusive terms inappropriate for a public incident system.</summary>
        public List<string> ProfanityTerms { get; set; } = new()
        {
            "fuck", "shit", "bitch", "bastard", "asshole", "dickhead", "cunt",
            "motherfucker", "faggot", "nigger", "nigga", "slut", "whore"
        };

        /// <summary>Explicitly sexual terms (acts / anatomy) unrelated to canal, drainage, or flood reporting.</summary>
        public List<string> ExplicitSexualTerms { get; set; } = new()
        {
            "porn", "xxx", "creampie", "cumshot", "blowjob", "handjob", "rimjob",
            "anilingus", "cunnilingus", "fellatio", "gangbang", "bukkake", "bukake",
            "deepthroat", "facesitting", "femdom", "hentai", "jizz", "squirt",
            "orgasm", "orgasmic", "vagina", "vaginal", "penis", "phallus", "cock",
            "dildo", "vibrator", "clitoris", "labia", "scrotum", "testicle",
            "testicles", "ballsack", "boner", "erection", "erect", "horny",
            "arousal", "aroused", "naked", "nude", "topless", "stripper",
            "prostitute", "escort", "brothel", "whorehouse"
        };

        /// <summary>
        /// Strong COMS-relevant vocabulary (English, Cebuano, Filipino). A
        /// description containing at least one strong term is treated as
        /// meaningfully related to canal / drainage / flood reporting.
        /// </summary>
        public List<string> ComsRelevantTerms { get; set; } = new()
        {
            // English
            "canal", "canals", "drain", "drainage", "drenasya", "flood",
            "flooded", "flooding", "floodwater", "blocked", "blockage",
            "obstruction", "obstructed", "overflowing", "overflow", "garbage",
            "debris", "sewage", "manhole", "estero", "silt", "sludge",
            "waterway", "culvert", "clogged", "clog", "sewer", "storm drain",
            "wastewater", "polluted", "pollution", "stagnant", "water level",
            "water rising", "rising water", "standing water", "inundated",
            "submerged", "waterlogged", "high tide",
            // Cebuano / Filipino
            "kanal", "baha", "bahaan", "pagbaha", "nabahaan", "basura", "tubig",
            "drenahin", "bumbog", "bumbong", "nasira", "nawalan", "lunod",
            "barado", "baradong", "imburnal", "imburnalo", "saluran",
            "padaluyan", "sira", "guba", "naguba", "putik", "sagbot",
            "naipon", "umuulan", "ulan", "maulan", "tumutulo", "butas",
            "apaw", "umaapaw", "pagtaas", "bumaha", "lubog", "lumubog",
            "lumulubog", "nalumos"
        };

        /// <summary>Supporting context terms (not sufficient alone for relevance).</summary>
        public List<string> ComsSupportingTerms { get; set; } = new()
        {
            "water", "street", "road", "trash", "rubbish", "waste", "rain",
            "raining", "heavy rain", "house", "neighborhood", "barangay",
            "sitio", "damaged", "crack", "leak", "leaking", "broken",
            "collapsed", "overflow", "spill"
        };

        /// <summary>
        /// Action/condition terms that become COMS-relevant when combined with
        /// a supporting context term (e.g. "water keeps rising").
        /// </summary>
        public List<string> ComsActionTerms { get; set; } = new()
        {
            "rising", "rise", "overflow", "overflowing", "spill", "spilling",
            "leak", "leaking", "entered", "entering", "reaches", "reaching",
            "high", "deep", "knee-deep", "waist-deep", "draining", "drain",
            "clogged", "blocked", "flooded"
        };

        /// <summary>Terms that mark a report as sanitation/drainage/flood-related context (blocklist context exception).</summary>
        public List<string> SanitationContextTerms { get; set; } = new()
        {
            "waste", "sewage", "septic", "septage", "toilet", "toilets", "latrine",
            "latrines", "manhole", "manholes", "drain", "drainage", "canal",
            "canals", "flood", "flooding", "floodwater", "garbage", "debris",
            "blockage", "blocked", "obstruction", "obstructed", "trash", "rubbish",
            "refuse", "sludge", "silt", "human waste", "fecal", "excrement"
        };

        public int MaxRepeatedCharRun { get; set; } = 4;        // 5+ identical consecutive chars = spam signal
        public int MinLengthForMashCheck { get; set; } = 6;     // don't mash-check tiny text
        public double MaxSymbolRatio { get; set; } = 0.50;      // symbols / alphanumerics above this = spam signal
        public double MinVowelRatioForMash { get; set; } = 0.20;// below this, long text is likely keyboard mashing
        public int MaxRepeatedNumericGroups { get; set; } = 2;  // 3+ repeated digit groups = spam signal
        public int MaxRepeatedWordCount { get; set; } = 2;      // 3+ identical words = repetition spam
        public int MinGibberishWordLength { get; set; } = 10;   // single-word gibberish check threshold
        public double MaxGibberishVowelRatio { get; set; } = 0.30; // above this, a long single word is not gibberish
    }

    public sealed class TextModerationResult
    {
        /// <summary>Accept / Reject / Uncertain.</summary>
        public string Status { get; set; } = "Accept";
        public List<string> Reasons { get; set; } = new();
        /// <summary>Suggested CommunityReport.Status: "Pending" (accepted) or "Under Review" (uncertain).</summary>
        public string SuggestedStatus { get; set; } = "Pending";
        public bool IsRejected => Status == "Reject";
        public bool IsUncertain => Status == "Uncertain";
    }

    public interface ITextModerationService
    {
        bool IsEnabled { get; }

        /// <summary>
        /// Moderates a report title and description together.
        /// </summary>
        TextModerationResult AnalyzeText(string? title, string? description);
    }

    public sealed class TextModerationService : ITextModerationService
    {
        private const string MeaningfulDescriptionMessage =
            "Please provide a meaningful description of the canal, drainage, or flooding issue.";

        private static readonly HashSet<string> RepetitionStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "is", "are", "was", "were", "be", "been",
            "being", "to", "of", "in", "on", "at", "for", "with", "near", "by",
            "from", "this", "that", "these", "those", "it", "its", "as", "but",
            "so", "we", "our", "us", "i", "my", "me", "you", "your", "he", "she",
            "they", "them", "their", "there", "here", "has", "have", "had", "do",
            "does", "did", "not", "no", "yes", "can", "could", "will", "would",
            "should", "may", "might", "must", "please", "very", "really", "just",
            "also", "ang", "ng", "mga", "sa", "na", "ay", "si", "ni", "kay",
            "para", "dahil", "kasi", "naman", "po", "din", "rin", "ito", "iyan",
            "iyon", "siya", "sila", "kami", "tayo", "ko", "mo", "niya", "nila",
            "namin", "natin", "inyo", "ngayon", "lang", "lamang", "yung", "ug",
            "ka", "ako", "ikaw", "among", "diri", "didto", "kini", "kana", "mao",
            "nga", "og", "alang", "nako", "nimo", "namo", "ninyo"
        };

        private readonly TextModerationOptions _options;

        public TextModerationService(IOptions<TextModerationOptions>? options = null)
        {
            _options = options?.Value ?? new TextModerationOptions();
        }

        public bool IsEnabled => _options.Enabled;

        public TextModerationResult AnalyzeText(string? title, string? description)
        {
            var result = new TextModerationResult();
            if (!_options.Enabled)
            {
                result.Status = "Accept";
                result.SuggestedStatus = "Pending";
                return result;
            }

            var titleNorm = Normalize(title);
            var descNorm = Normalize(description);
            var combined = (titleNorm + " " + descNorm).Trim();

            // ---- Title presence ----
            if (string.IsNullOrWhiteSpace(titleNorm))
            {
                result.Status = "Reject";
                result.Reasons.Add("A meaningful title is required.");
                result.SuggestedStatus = "Pending";
                return result;
            }

            // ---- Description presence ----
            if (string.IsNullOrWhiteSpace(descNorm))
            {
                result.Status = "Reject";
                result.Reasons.Add(MeaningfulDescriptionMessage);
                result.SuggestedStatus = "Pending";
                return result;
            }

            var signals = new List<string>();
            var strongReject = false;

            AnalyzeSpamHeuristics(titleNorm, descNorm, combined, signals);

            // ---- Blocklist (profane / explicitly sexual) ----
            var blocklistHits = FindBlocklistHits(combined);
            if (blocklistHits.Count > 0)
            {
                var sanitationContext = HasSanitationContext(combined);
                var contextualHits = blocklistHits
                    .Where(h => !sanitationContext || !IsSanitationRelatedTerm(h))
                    .ToList();

                if (contextualHits.Count > 0)
                {
                    strongReject = true;
                    signals.Add($"Blocked content detected ({string.Join(", ", contextualHits)}).");
                }
                else
                {
                    signals.Add($"Potentially sensitive term used in a sanitation context ({string.Join(", ", blocklistHits)}); flagged for review.");
                }
            }

            // ---- COMS relevance ----
            // The description is what staff rely on, so acceptance requires the
            // description itself to mention a canal, drainage, or flood concern.
            // A relevant title alone is not enough to accept an off-topic body.
            var descriptionRelevant = HasComsRelevance(descNorm);

            // ---- Decision ----
            var spamSignals = signals.Where(IsSpamSignal).ToList();

            if (strongReject)
            {
                result.Status = "Reject";
                result.SuggestedStatus = "Pending";
            }
            else if (spamSignals.Any(IsStrongSpamSignal))
            {
                // Confident nonsense (gibberish / keyboard mashing / heavy
                // repetition / symbols) is not a meaningful report.
                result.Status = "Reject";
                result.SuggestedStatus = "Pending";
            }
            else if (spamSignals.Count >= 2)
            {
                result.Status = "Reject";
                result.SuggestedStatus = "Pending";
            }
            else if (descriptionRelevant)
            {
                // Meaningful language that relates to COMS reporting.
                result.Status = "Accept";
                result.SuggestedStatus = "Pending";
            }
            else
            {
                // The text is not confidently nonsense, but it does not clearly
                // describe a COMS-related issue either. Send it to staff review
                // instead of rejecting a possibly legitimate local-language or
                // misspelled report.
                result.Status = "Uncertain";
                result.SuggestedStatus = "Under Review";
            }

            result.Reasons.AddRange(signals);
            return result;
        }

        private static bool IsSpamSignal(string signal) =>
            signal.StartsWith("Repeated character", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Repeated numeric", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Repeated word", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Keyboard mashing", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Keyboard row pattern", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Gibberish", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("High symbol ratio", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("All-caps title", StringComparison.OrdinalIgnoreCase);

        private static bool IsStrongSpamSignal(string signal) =>
            signal.StartsWith("Repeated character", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Repeated numeric", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Repeated word", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Keyboard mashing", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Keyboard row pattern", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("Gibberish", StringComparison.OrdinalIgnoreCase) ||
            signal.StartsWith("High symbol ratio", StringComparison.OrdinalIgnoreCase);

        private static string Normalize(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : string.Join(' ', text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private void AnalyzeSpamHeuristics(string title, string description, string combined, List<string> signals)
        {
            // Repeated identical character run (e.g. "!!!!!", "aaaaaa", "11111").
            foreach (var text in new[] { title, description, combined })
            {
                if (HasRepeatedCharRun(text, _options.MaxRepeatedCharRun + 1))
                {
                    signals.Add("Repeated characters detected — content looks like spam or keyboard mashing.");
                    break;
                }
            }

            // Repeated numeric string (e.g. "123123123", "5555555555").
            foreach (var text in new[] { title, description, combined })
            {
                if (HasRepeatedNumericGroups(text, _options.MaxRepeatedNumericGroups + 1))
                {
                    signals.Add("Repeated numeric string detected — content looks like spam.");
                    break;
                }
            }

            // Repeated word (e.g. "Hello hello hello hello hello").
            if (HasRepeatedWord(description, _options.MaxRepeatedWordCount + 1)
                || HasRepeatedWord(title, _options.MaxRepeatedWordCount + 1))
            {
                signals.Add("Repeated word detected — description does not communicate a real problem.");
            }

            // Gibberish tokens (e.g. "WJENFIWJMDEQUGHWRFWEF"). The title and
            // description are checked separately so a meaningful title cannot
            // hide a nonsense description.
            if (IsMostlyGibberish(title) || IsMostlyGibberish(description))
            {
                signals.Add("Gibberish detected — content does not form meaningful words.");
            }

            // Keyboard mashing / row patterns (e.g. "asdfghjkl qwertyuiop zxcvbnm").
            if ((combined.Length >= _options.MinLengthForMashCheck && IsLikelyKeyboardMashing(combined))
                || IsLikelyKeyboardMashing(description)
                || IsLikelyKeyboardMashing(title))
            {
                signals.Add("Keyboard mashing detected — no meaningful content found.");
            }

            // Obvious keyboard-row sequences (e.g. "asdfghjkl", "qwertyuiop").
            if (HasKeyboardRowSequence(combined))
            {
                signals.Add("Keyboard row pattern detected — content does not form meaningful words.");
            }

            // High symbol / punctuation ratio (e.g. "!!!@#$%%^").
            var symbolRatio = SymbolRatio(combined);
            if (symbolRatio > _options.MaxSymbolRatio && combined.Length >= _options.MinLengthForMashCheck)
            {
                signals.Add("High symbol ratio — content does not look like a meaningful report.");
            }

            // All-caps shouting on longer titles.
            if (title.Length >= 8 && title.All(char.IsLetter) && title == title.ToUpperInvariant())
            {
                signals.Add("All-caps title — please use normal capitalization.");
            }
        }

        /// <summary>
        /// Detects a description made mostly of implausible letter sequences
        /// (e.g. a long consonant-heavy token). Tokens are evaluated rather
        /// than requiring a fixed blocklist, so unseen keyboard mashing is
        /// caught while ordinary words, names, and numbers are left alone.
        /// </summary>
        private bool IsMostlyGibberish(string text)
        {
            var tokens = Tokenize(text, includeDigits: false);
            if (tokens.Count == 0) return false;

            var suspicious = tokens.Count(IsSuspiciousToken);
            if (suspicious == 0) return false;

            if (tokens.Count == 1) return true;

            return suspicious >= 2 && (double)suspicious / tokens.Count >= 0.60;
        }

        private bool IsSuspiciousToken(string token)
        {
            if (token.Length == 0) return false;

            if (HasKeyboardRowSequence(token)) return true;

            if (token.Length >= 4 && IsRepeatedPattern(token.AsSpan(), 3)) return true;

            var ratio = VowelRatio(token);

            if (token.Length >= _options.MinGibberishWordLength
                && ratio < _options.MaxGibberishVowelRatio)
            {
                return true;
            }

            return token.Length >= 6 && ratio < 0.01 && !IsAllUpper(token);
        }

        /// <summary>
        /// Detects obvious keyboard-row sequences (top / home / bottom rows)
        /// which are a strong indicator of keyboard mashing rather than a
        /// meaningful report.
        /// </summary>
        private static readonly string[] KeyboardRowMashes =
        {
            "qwertyuiop", "asdfghjkl", "zxcvbnm", "mnbvcxz", "lkjhgfdsa",
            "poiuytrewq", "qwert", "werty", "ertyu", "rtyui", "tyuio",
            "yuiop", "asdfg", "sdfgh", "dfghj", "fghjk", "ghjkl", "zxcvb",
            "xcvbn", "cvbnm"
        };

        private static readonly string[] KeyboardRowTokens =
        {
            "qwer", "rtyu", "tyui", "yuio", "uiop", "asdf", "sdfg", "dfgh",
            "fghj", "ghjk", "hjkl", "zxcv", "xcvb", "cvbn", "vbnm"
        };

        private static bool HasKeyboardRowSequence(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var lower = text.ToLowerInvariant();
            if (KeyboardRowMashes.Any(lower.Contains)) return true;
            return Tokenize(lower, includeDigits: false).Any(KeyboardRowTokens.Contains);
        }

        private static bool HasRepeatedWord(string text, int minRepeats)
        {
            if (string.IsNullOrEmpty(text) || minRepeats <= 1) return false;

            var words = Tokenize(text, includeDigits: true)
                .Where(w => w.Length >= 2 && !RepetitionStopWords.Contains(w))
                .ToList();

            if (words.Count < minRepeats) return false;

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in words)
            {
                counts.TryGetValue(word, out var count);
                counts[word] = count + 1;
            }

            var mostRepeated = counts.Values.Max();
            return mostRepeated >= minRepeats
                && (double)mostRepeated / words.Count >= 0.60;
        }

        private bool IsLikelyKeyboardMashing(string text)
        {
            var letters = text.Where(char.IsLetter).ToList();
            if (letters.Count == 0) return false;
            var vowels = letters.Count(IsVowel);
            var vowelRatio = (double)vowels / letters.Count;
            return vowelRatio < _options.MinVowelRatioForMash
                && letters.Count >= _options.MinLengthForMashCheck;
        }

        /// <summary>
        /// Returns true when the text contains strong COMS-relevant vocabulary
        /// (canal / drainage / flood / garbage / debris / sewage / etc. in
        /// English, Cebuano, or Filipino). A small edit-distance check tolerates
        /// ordinary spelling mistakes without requiring a dictionary.
        /// </summary>
        private bool HasComsRelevance(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var lower = text.ToLowerInvariant();

            if (_options.ComsRelevantTerms.Any(t =>
                !string.IsNullOrWhiteSpace(t) && lower.Contains(t.Trim().ToLowerInvariant())))
            {
                return true;
            }

            if (ContainsAnyTerm(lower, _options.ComsSupportingTerms)
                && ContainsAnyTerm(lower, _options.ComsActionTerms))
            {
                return true;
            }

            var tokens = Tokenize(text, includeDigits: false)
                .Select(t => t.ToLowerInvariant())
                .ToList();

            foreach (var term in _options.ComsRelevantTerms)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;
                var cleanTerm = term.Trim().ToLowerInvariant();
                if (cleanTerm.Length < 5) continue;

                foreach (var token in tokens)
                {
                    if (token.Length < 4) continue;
                    var allowedDistance = cleanTerm.Length >= 6 ? 2 : 1;
                    if (Math.Abs(token.Length - cleanTerm.Length) > allowedDistance) continue;
                    if (LevenshteinDistance(token, cleanTerm, allowedDistance) <= allowedDistance)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ContainsAnyTerm(string lowerText, IEnumerable<string> terms) =>
            terms.Any(t => !string.IsNullOrWhiteSpace(t) && lowerText.Contains(t.Trim().ToLowerInvariant()));

        private static int LevenshteinDistance(string a, string b, int maxDistance)
        {
            if (Math.Abs(a.Length - b.Length) > maxDistance) return maxDistance + 1;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                var rowMin = current[0];
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(
                        Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + cost);
                    rowMin = Math.Min(rowMin, current[j]);
                }
                if (rowMin > maxDistance) return maxDistance + 1;
                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }

        private static List<string> Tokenize(string text, bool includeDigits)
        {
            var tokens = new List<string>();
            var buffer = new StringBuilder();
            foreach (var c in text)
            {
                if (char.IsLetter(c) || (includeDigits && char.IsDigit(c)))
                {
                    buffer.Append(c);
                }
                else if (buffer.Length > 0)
                {
                    tokens.Add(buffer.ToString());
                    buffer.Clear();
                }
            }
            if (buffer.Length > 0) tokens.Add(buffer.ToString());
            return tokens;
        }

        private static bool HasRepeatedCharRun(string text, int run)
        {
            if (string.IsNullOrEmpty(text) || run <= 1) return false;
            var count = 1;
            for (var i = 1; i < text.Length; i++)
            {
                if (text[i] == text[i - 1])
                {
                    count++;
                    if (count >= run) return true;
                }
                else
                {
                    count = 1;
                }
            }
            return false;
        }

        private static bool HasRepeatedNumericGroups(string text, int minRepeats)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var i = 0;
            while (i < text.Length)
            {
                if (!char.IsDigit(text[i]))
                {
                    i++;
                    continue;
                }
                var j = i;
                while (j < text.Length && char.IsDigit(text[j])) j++;
                var run = text.AsSpan(i, j - i);
                if (run.Length >= 2 && IsRepeatedPattern(run, minRepeats))
                {
                    return true;
                }
                i = j;
            }
            return false;
        }

        private static bool IsRepeatedPattern(ReadOnlySpan<char> s, int minRepeats)
        {
            for (var unitLen = 1; unitLen <= s.Length / 2; unitLen++)
            {
                if (s.Length % unitLen != 0) continue;
                var repeats = s.Length / unitLen;
                if (repeats < minRepeats) continue;
                var unit = s.Slice(0, unitLen);
                var ok = true;
                for (var k = 1; k < repeats; k++)
                {
                    if (!s.Slice(k * unitLen, unitLen).SequenceEqual(unit))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) return true;
            }
            return false;
        }

        private static bool IsAllUpper(string token) =>
            token.Any(char.IsLetter) && token.All(c => !char.IsLetter(c) || char.IsUpper(c));

        private static bool IsVowel(char c) =>
            "aeiouyAEIOUYáéíóúàèìòùâêîôûäëïöü".Contains(c);

        private static double VowelRatio(string word)
        {
            if (string.IsNullOrEmpty(word)) return 0;
            var letters = word.Where(char.IsLetter).ToList();
            if (letters.Count == 0) return 0;
            var vowels = letters.Count(IsVowel);
            return (double)vowels / letters.Count;
        }

        private static double SymbolRatio(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var alphanumeric = text.Count(char.IsLetterOrDigit);
            if (alphanumeric == 0) return 1.0;
            var symbols = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
            return (double)symbols / (alphanumeric + symbols);
        }

        private List<string> FindBlocklistHits(string text)
        {
            var lower = text.ToLowerInvariant();
            var hits = new List<string>();
            foreach (var term in _options.ProfanityTerms.Concat(_options.ExplicitSexualTerms))
            {
                if (!string.IsNullOrEmpty(term) && lower.Contains(term.ToLowerInvariant()))
                {
                    hits.Add(term);
                }
            }
            return hits;
        }

        private bool HasSanitationContext(string text)
        {
            var lower = text.ToLowerInvariant();
            return _options.SanitationContextTerms.Any(t => !string.IsNullOrEmpty(t) && lower.Contains(t.ToLowerInvariant()));
        }

        private bool IsSanitationRelatedTerm(string term)
        {
            var t = term.ToLowerInvariant();
            return _options.SanitationContextTerms.Any(s =>
                !string.IsNullOrEmpty(s) && t.Equals(s.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
        }
    }
}
