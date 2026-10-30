using System.Text.RegularExpressions;

namespace COMS_MVC.Services
{
    /// <summary>
    /// Strict flood-relevance classifier for the Flood News feature.
    /// An article is shown ONLY when flooding is its primary subject or a
    /// substantial part of the report. General weather, politics, budget
    /// processes, sports, entertainment, crime, unrelated disasters, and
    /// incidental/metaphorical "flood" mentions are rejected. In particular,
    /// political/budget-process framing (including transparency/watchdog
    /// fights over flood-control funds) without flood-event substance is
    /// politics â€” not flood news â€” and a bare "flood control" mention counts
    /// only beside concrete prevention-work evidence. Pure function of
    /// title+body text, so it is unit-testable and applied identically on
    /// initial load, refresh, cached results, and background fetches (the
    /// service calls it at the source, before save/cache/count/display).
    /// </summary>
    public static class FloodRelevance
    {
        // Unambiguous flood language. Single words match on word boundaries;
        // phrases match by containment. Filipino/Cebuano terms included.
        private static readonly string[] StrongTerms =
        {
            "flash flood", "flash floods", "flash flooding",
            "flood warning", "flood warnings", "flood advisory", "flood advisories",
            "flood alert", "flood alerts", "flood risk", "flood risks",
            "floodwater", "floodwaters", "flood water", "flood waters",
            "storm surge", "storm surges",
            "river overflow", "rivers overflow", "river overflows",
            "overflowing river", "overflowing rivers", "overflowing creek",
            "overflows its banks", "overflowed its banks", "burst its banks",
            "drainage overflow", "canal overflow", "estero overflow",
            "urban flooding", "widespread flooding", "massive flooding",
            "heavy flooding", "severe flooding", "deadly flooding",
            "flood-prone", "flood prone",
            "submerged", "inundation", "inundated", "waterlogging",
            "waist-deep", "knee-deep", "neck-deep", "chest-deep", "gutter-deep",
            "pagbaha", "binaha", "binabaha", "babahain", "baha", "lunop",
            "declogging", "desilting"
        };

        // Flood damage / breached-infrastructure language: real event substance
        // even without the word "flood" nearby (e.g. a breached dike).
        private static readonly string[] DamageTerms =
        {
            "flood damage", "flood-damaged", "damaged by flood", "damaged by floods",
            "destroyed by flood", "destroyed by floods", "flood-hit", "flood hit",
            "flood-affected", "flood victims", "flood evacuees", "displaced by flood",
            "dike breach", "breached dike", "collapsed dike", "damaged dike",
            "broken dike", "levee breach", "embankment collapse",
            "dam break", "dam burst", "dam collapse"
        };

        // Flood-control / mitigation project language: relevant ONLY as
        // actual prevention work (see WorkTerms) â€” but NOT when the framing
        // is political / budget-process without flood-event substance
        // (see veto below).
        private static readonly string[] ProjectTerms =
        {
            "flood control", "flood mitigation"
        };

        // Concrete prevention-work language. A "flood control" mention counts
        // only beside evidence of real work â€” a budget speech that merely says
        // the words is process news, not flood news.
        private static readonly string[] WorkTerms =
        {
            "project", "projects", "construct", "constructed", "construction",
            "build", "built", "building",
            "rehabilitate", "rehabilitation",
            "declogging", "desilting",
            "widening", "pump", "pumping station",
            "embankment", "drainage", "floodgate", "dike", "river wall",
            "completed", "completion", "launched", "groundbreaking",
            "inaugurated", "inauguration", "repair", "repaired", "retrofitting"
        };

        // Political / budget-process framing. When present WITHOUT
        // flood-event substance, the story is politics â€” not flood news â€”
        // even if it says "flood control" (e.g. budget-transparency fights
        // over flood-control allocations). (A genuine flood event described
        // in the same article still counts; event evidence always wins.)
        // NOTE: single words match on word boundaries, so full word forms
        // are listed explicitly â€” bare stems never match.
        private static readonly string[] AllegationMarkers =
        {
            "allegation", "allegations", "alleged", "allege",
            "corrupt", "corruption", "corruptions",
            "graft", "plunder", "kickback", "kickbacks", "bribe", "bribery",
            "ghost", "ghost project", "ghost projects",
            "overpriced", "overpricing",
            "anomaly", "anomalies", "anomalous",
            "irregularity", "irregularities",
            "controversy", "controversies", "controversial",
            "scandal", "scandals", "impeach", "impeachment",
            "accusation", "accusations", "accused",
            "wrongdoing", "misuse",
            "transparency", "watchdog", "watchdogs",
            "budget", "appropriation", "appropriations",
            "allocation", "allocations", "itemization",
            "deliberation", "deliberations"
        };

        // Bare flood words: meaningful only with context (or as title subject).
        private static readonly string[] FloodWords =
        {
            "flood", "floods", "flooding", "flooded"
        };

        // "Overflow" alone is ambiguous ("overflow crowd"): needs water context.
        private static readonly string[] OverflowWords =
        {
            "overflow", "overflows", "overflowed", "overflowing"
        };

        // Water / disaster-response context that grounds bare flood/overflow words.
        // (Deliberately excludes the flood/overflow words themselves so the
        // context check is never trivially self-satisfied.)
        private static readonly string[] WaterContext =
        {
            "river", "rivers", "creek", "tributary", "watershed", "spillway",
            "drainage", "canal", "estero", "waterway", "dam", "dike", "embankment",
            "riverbank", "river bank", "rain", "monsoon",
            "habagat", "typhoon", "storm", "surge", "downpour", "deluge",
            "rescue", "evacuate", "evacuated", "evacuation", "evacuations", "evacuating", "stranded", "displaced", "marooned",
            "barangay", "tubig"
        };

        // Weather words that are NEVER enough on their own.
        private static readonly string[] WeakTerms =
        {
            "heavy rainfall", "heavy rain", "heavy rains", "torrential rain",
            "torrential rains", "intense rainfall", "rainfall warning",
            "rainfall", "rains", "rain", "ulan",
            "typhoon", "bagyo", "tropical storm", "cyclone", "storm", "storms",
            "monsoon", "southwest monsoon", "habagat", "amihan", "itcz",
            "thunderstorm", "thunderstorms",
            "water level", "water levels", "drainage", "canal",
            "evacuation", "evacuations", "evacuate", "evacuated", "evacuating"
        };

        // Response/risk/damage markers that make a weak weather combo a real advisory.
        private static readonly string[] AdvisoryMarkers =
        {
            "warning", "advisory", "alert", "evacuate", "evacuated", "evacuation", "evacuations", "evacuating", "red rainfall",
            "orange rainfall", "yellow rainfall", "signal no", "wind signal",
            "preemptive", "forced evacuation", "suspend", "state of calamity",
            "damage", "damaged", "destroyed"
        };

        // Topics that veto weak/body-only acceptance (never veto strong terms
        // or a flood-word title subject â€” e.g. quake survivors facing floods).
        private static readonly string[] ExclusionTopics =
        {
            "earthquake", "magnitude", "aftershock", "volcanic", "volcano eruption",
            "eruption", "lava", "ashfall", "phivolcs",
            "election", "campaign", "senatorial", "plebiscite", "impeachment",
            "championship", "basketball", "volleyball", "boxing match", "olympics",
            "sea games", "pba", "mpbl",
            "showbiz", "concert", "teleserye", "celebrity",
            "murder", "homicide", "robbery", "holdup", "drug bust", "shabu",
            "stock market", "psei", "peso exchange"
        };

        private static readonly Regex NegationRegex = new(
            @"\b(without|no |not |never |unrelated|denies?|denied|dismisses?|dismissed|rules out|ruled out|no connection)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MetaphorRegex = new(
            @"\bflood of (support|complaints?|criticism|messages?|calls?|tributes?|applause|rumou?rs?|speculation|condolences)\b" +
            @"|\bflooded with (messages?|calls?|complaints?|support|inquiries|requests)\b" +
            @"|\bflood the market\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AdvisoryLabelRegex = new(
            @"\b(warning|advisory|alert|watch|forecast|outlook|expected|may experience|could experience|braces? for|prepares? for)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] CebuPriorityAreas =
        {
            "Cebu City", "Lapu-Lapu", "Mandaue", "Talisay City", "Consolacion",
            "Liloan", "Compostela", "Cordova", "Minglanilla", "Danao City",
            "Toledo City", "Carcar City", "Naga City", "Metro Cebu", "Cebu",
            "Central Visayas", "Bohol", "Negros", "Leyte", "Samar", "Biliran",
            "Siquijor", "Ormoc City", "Tacloban City", "Tagbilaran City"
        };

        // Foreign countries / cities / regions. Used to catch foreign-flood
        // stories (and international roundups) that merely mention the
        // Philippines incidentally. PH towns that collide with country names
        // (e.g. Mexico, Pampanga) are deliberately NOT gazetteer entries.
        private static readonly string[] ForeignPlaces =
        {
            "united states", "america", "american", "new york", "texas",
            "florida", "california", "washington dc",
            "china", "chinese", "beijing", "shanghai", "hong kong",
            "japan", "japanese", "tokyo", "osaka",
            "korea", "seoul", "taiwan", "taipei", "taiwanese",
            "india", "indian", "delhi", "mumbai", "kolkata", "chennai",
            "pakistan", "pakistani", "karachi", "lahore",
            "bangladesh", "bangladeshi", "dhaka",
            "indonesia", "indonesian", "jakarta", "bali",
            "malaysia", "malaysian", "kuala lumpur", "singapore",
            "thailand", "thai", "bangkok", "vietnam", "vietnamese", "hanoi",
            "myanmar", "burma", "cambodia", "laos", "nepal", "kathmandu",
            "sri lanka", "afghanistan", "mongolia",
            "australia", "australian", "sydney", "melbourne",
            "new zealand", "europe", "european", "britain", "british",
            "london", "england", "france", "french", "paris", "germany",
            "german", "berlin", "italy", "italian", "rome", "venice",
            "spain", "spanish", "madrid", "portugal", "greece", "greek",
            "netherlands", "dutch", "belgium", "poland", "ukraine", "russia",
            "turkey", "turkish", "egypt", "libya", "morocco", "algeria",
            "nigeria", "kenya", "ethiopia", "somalia", "sudan",
            "south africa", "africa", "african",
            "saudi", "arabia", "dubai", "uae", "qatar", "kuwait", "iran",
            "iraq", "israel", "jordan", "lebanon", "yemen", "oman",
            "middle east", "canada", "canadian", "toronto", "vancouver",
            "mexico", "mexican", "brazil", "brazilian", "argentina",
            "chile", "peru", "colombia", "venezuela", "cuba", "haiti",
            "asia", "asian", "southeast asia"
        };

        /// <summary>
        /// Philippines-focus verdict for articles with NO specific PH place.
        /// True only with an explicit country mention that is not drowned out
        /// by foreign locations (foreign floods and international roundups
        /// that mention the Philippines incidentally are rejected).
        /// </summary>
        public static bool HasPhilippineFocus(string? text)
        {
            var lower = (text ?? string.Empty).ToLowerInvariant();
            if (lower.Length == 0)
                return false;
            // Strict country mention (bare "ph" excluded: matches "pH level").
            var generic = Regex.Matches(lower,
                @"\b(philippines|philippine|filipino|filipina|pilipinas|phl)\b").Count;
            if (generic == 0)
                return false;
            var foreign = ForeignPlaces.Count(k => Matches(lower, k));
            // With no specific PH place, an international story that happens
            // to mention the Philippines must not pass. Require PH mentions to
            // clearly outnumber foreign locations, not merely equal them.
            if (foreign >= 1 && generic <= foreign)
                return false;
            return true;
        }

        /// <summary>
        /// Strict relevance verdict over title + body text.
        /// Evidence is evaluated per sentence so that negated mentions
        /// ("without any flood-prevention connection", "no flooding reported")
        /// never count as flood substance.
        /// </summary>
        public static bool IsRelevant(string? title, string? body)
        {
            var t = (title ?? string.Empty).Trim();
            var b = (body ?? string.Empty).Trim();
            if (t.Length == 0 && b.Length == 0)
                return false;
            var combined = t + "\n" + b;
            var lowerAll = combined.ToLowerInvariant();
            var excludedTopic = ExclusionTopics.Any(k => Matches(lowerAll, k));

            // Clear (non-negated) sentences carry evidence; negated ones don't.
            var sentences = Regex.Split(combined, @"[.?!;\n]+")
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Select(s => new { Text = s, Lower = s.ToLowerInvariant() })
                .Where(s => !NegationRegex.IsMatch(s.Text))
                .ToList();
            if (sentences.Count == 0)
                return false;

            // 1. Genuine flood-event substance in a clear sentence: accept.
            //    Always wins â€” even inside political coverage, documented
            //    flood events, damage, or warnings are real flood news.
            if (sentences.Any(s => StrongTerms.Any(k => Matches(s.Lower, k))
                || DamageTerms.Any(k => Matches(s.Lower, k))))
                return true;

            // 1b. Political/corruption-allegation framing WITHOUT flood-event
            //     substance above is politics, not flood news â€” even when it
            //     says "flood control". Reject here.
            if (AllegationMarkers.Any(k => Matches(lowerAll, k)))
                return false;

            // 1c. Flood-control / mitigation project language in a clear
            //     sentence (reached only when no political/budget-process
            //     framing applies) PLUS concrete prevention-work evidence in
            //     the same sentence. A passing mention in a speech or budget
            //     line-item is not flood news on its own.
            if (sentences.Any(s => ProjectTerms.Any(k => Matches(s.Lower, k))
                && WorkTerms.Any(k => Matches(s.Lower, k))))
                return true;

            // 2. Flood word as the TITLE subject (titles summarize the story):
            //    accept unless the title itself is metaphorical
            //    ("Flood of support...", "City flooded with calls").
            var clearTitle = string.Join(" ",
                Regex.Split(t, @"[.?!;\n]+").Where(s => !NegationRegex.IsMatch(s)));
            if (!MetaphorRegex.IsMatch(clearTitle)
                && FloodWords.Any(k => Matches(clearTitle.ToLowerInvariant(), k)))
                return true;

            // 3. Overflow words need water context in the SAME sentence.
            if (sentences.Any(s => OverflowWords.Any(k => Matches(s.Lower, k))
                && WaterContext.Any(k => Matches(s.Lower, k))))
                return true;

            // 4. Body-only bare flood words need water/response context in the
            //    same sentence and must not sit in an unrelated-topic article.
            if (!excludedTopic && sentences.Any(s => FloodWords.Any(k => Matches(s.Lower, k))
                && WaterContext.Any(k => Matches(s.Lower, k))))
                return true;

            // 5. Weak weather combos: only with an advisory/response marker,
            //    and never inside an excluded-topic article.
            if (!excludedTopic)
            {
                var lower = combined.ToLowerInvariant();
                var weakHits = WeakTerms.Count(k => Matches(lower, k));
                if (weakHits >= 2 && AdvisoryMarkers.Any(k => Matches(lower, k)))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Presentation label derived from the article's own words:
        /// "Advisory" when it talks about warnings/forecasts, else "Report".
        /// Never invents severity or locations.
        /// </summary>
        public static string TopicLabel(string? title, string? snippet) =>
            AdvisoryLabelRegex.IsMatch(((title ?? string.Empty) + "\n" + (snippet ?? string.Empty)))
                ? "Advisory"
                : "Report";

        /// <summary>Cebu City + nearby communities sort first (same recency).</summary>
        public static bool IsCebuPriority(IEnumerable<string> affectedAreas)
        {
            foreach (var area in affectedAreas)
            {
                if (CebuPriorityAreas.Any(c =>
                    area.Contains(c, StringComparison.OrdinalIgnoreCase) ||
                    c.Contains(area, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Dedup key shared by URL and title tracking: same URL (ignoring
        /// case/trailing slash) or same normalized title counts as duplicate,
        /// catching the same story from different providers.
        /// </summary>
        public static string DedupKey(string? url, string? title)
        {
            var u = (url ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
            if (u.Length > 0)
                return "url:" + u;
            var t = Regex.Replace((title ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", "");
            return "title:" + t;
        }

        private static bool Matches(string lowerText, string keyword) =>
            keyword.Contains(' ')
                ? lowerText.Contains(keyword, StringComparison.Ordinal)
                : Regex.IsMatch(lowerText, @"\b" + Regex.Escape(keyword) + @"\b");
    }
}

