#nullable disable
#pragma warning disable CS0618

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityLeanMcp
{
    public static partial class Inspect
    {
        private struct AuditSection
        {
            public string Title;
            public List<string> Items;
            public int CollectionSuppressed;
            public int ShownCount;
            public int TotalOmitted;
            public int FullLength;
            public int Budget;
        }

        private static string FormatAuditReport(AuditReport report)
        {
            if (report.TotalIssues == 0 && report.TotalSuppressed == 0)
            {
                var emptySb = new StringBuilder();
                emptySb.AppendLine(FormattableString.Invariant($"Audit of {report.ScopeDescription}: 0 issues found ({report.GameObjectsScanned} GameObject(s) scanned)."));
                if (!string.IsNullOrEmpty(report.TruncationNote))
                {
                    emptySb.AppendLine();
                    emptySb.AppendLine(report.TruncationNote);
                }
                return LimitOutput(emptySb.ToString().TrimEnd());
            }

            int nlLen = Environment.NewLine.Length;
            var sections = new[]
            {
                new AuditSection { Title = "Missing Scripts", Items = report.MissingScripts, CollectionSuppressed = report.MissingScriptsSuppressed },
                new AuditSection { Title = "Broken Object References", Items = report.BrokenReferences, CollectionSuppressed = report.BrokenReferencesSuppressed },
                new AuditSection { Title = "Broken UnityEvent Listeners", Items = report.BrokenUnityEvents, CollectionSuppressed = report.BrokenUnityEventsSuppressed },
                new AuditSection { Title = "Material & Shader Errors", Items = report.MaterialAndShaderIssues, CollectionSuppressed = report.MaterialAndShaderIssuesSuppressed },
                new AuditSection { Title = "Scene Hygiene Issues", Items = report.SceneHygieneIssues, CollectionSuppressed = report.SceneHygieneIssuesSuppressed }
            };

            int activeCount = 0;
            int totalFullLength = 0;
            for (int i = 0; i < sections.Length; i++)
            {
                int itemCount = sections[i].Items != null ? sections[i].Items.Count : 0;
                int suppressed = sections[i].CollectionSuppressed;
                if (itemCount == 0 && suppressed == 0)
                {
                    continue;
                }

                activeCount++;
                string fullCountText = suppressed > 0
                    ? FormattableString.Invariant($"{itemCount}+, {suppressed} omitted")
                    : itemCount.ToString(CultureInfo.InvariantCulture);
                int len = nlLen + $"{sections[i].Title} ({fullCountText}):".Length + nlLen;
                for (int j = 0; j < itemCount; j++)
                {
                    len += (sections[i].Items[j] != null ? sections[i].Items[j].Length : 0) + nlLen;
                }

                if (suppressed > 0)
                {
                    len += GetSectionTruncationMarker(suppressed, report.MaxIssuesPerCategory, outputBudgetReached: false).Length + nlLen;
                }

                sections[i].FullLength = len;
                totalFullLength += len;
            }

            int headerReserve =
                FormattableString.Invariant($"Audit of {report.ScopeDescription}: 999999 issue(s) reported (999999 additional omitted) ({report.GameObjectsScanned} GameObject(s) scanned):").Length + nlLen +
                "Category counts: Missing Scripts: 999999+ (999999 omitted), Broken Object References: 999999+ (999999 omitted), Broken UnityEvent Listeners: 999999+ (999999 omitted), Material & Shader Errors: 999999+ (999999 omitted), Scene Hygiene Issues: 999999+ (999999 omitted)".Length + nlLen +
                (string.IsNullOrEmpty(report.TruncationNote) ? 0 : nlLen + report.TruncationNote.Length + nlLen) +
                nlLen + OutputTruncationMarker.Length;

            int sectionsBudget = Math.Max(0, DefaultMaxOutputCharacters - headerReserve);
            bool anyOmittedByFormat = false;

            if (totalFullLength <= sectionsBudget)
            {
                for (int i = 0; i < sections.Length; i++)
                {
                    if (sections[i].FullLength == 0) continue;
                    sections[i].ShownCount = sections[i].Items.Count;
                    sections[i].TotalOmitted = sections[i].CollectionSuppressed;
                }
            }
            else
            {
                anyOmittedByFormat = true;
                AllocateSectionBudgets(sections, activeCount, sectionsBudget);

                for (int i = 0; i < sections.Length; i++)
                {
                    if (sections[i].FullLength == 0) continue;

                    int itemCount = sections[i].Items.Count;
                    if (sections[i].FullLength <= sections[i].Budget)
                    {
                        sections[i].ShownCount = itemCount;
                        sections[i].TotalOmitted = sections[i].CollectionSuppressed;
                        continue;
                    }

                    int maxPossibleOmitted = itemCount + sections[i].CollectionSuppressed;
                    int worstHeaderLen = FormattableString.Invariant($"{sections[i].Title} ({itemCount}+, {maxPossibleOmitted} omitted):").Length + nlLen;
                    int worstMarkerLen = GetSectionTruncationMarker(maxPossibleOmitted, report.MaxIssuesPerCategory, outputBudgetReached: true).Length + nlLen;
                    int used = nlLen + worstHeaderLen + worstMarkerLen;

                    int shown = 0;
                    for (int j = 0; j < itemCount; j++)
                    {
                        int lineLen = (sections[i].Items[j] != null ? sections[i].Items[j].Length : 0) + nlLen;
                        if (used + lineLen <= sections[i].Budget)
                        {
                            used += lineLen;
                            shown++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    sections[i].ShownCount = shown;
                    sections[i].TotalOmitted = sections[i].CollectionSuppressed + (itemCount - shown);
                }
            }

            int totalShown = 0;
            int totalOmitted = 0;
            for (int i = 0; i < sections.Length; i++)
            {
                totalShown += sections[i].ShownCount;
                totalOmitted += sections[i].TotalOmitted;
            }

            var sb = new StringBuilder();
            string issueSummary = totalOmitted > 0
                ? FormattableString.Invariant($"{totalShown} issue(s) reported ({totalOmitted} additional omitted)")
                : FormattableString.Invariant($"{totalShown} issue(s) found");
            sb.AppendLine(FormattableString.Invariant($"Audit of {report.ScopeDescription}: {issueSummary} ({report.GameObjectsScanned} GameObject(s) scanned):"));
            sb.AppendLine($"Category counts: {FormatCategorySummary(sections)}");

            for (int i = 0; i < sections.Length; i++)
            {
                if (sections[i].FullLength == 0) continue;

                bool truncatedByBudget = sections[i].ShownCount < sections[i].Items.Count ||
                    (sections[i].CollectionSuppressed > 0 && report.IsOutputBudgetReached && sections[i].ShownCount < report.MaxIssuesPerCategory);
                AppendAuditSection(
                    sb,
                    sections[i].Title,
                    sections[i].Items,
                    sections[i].ShownCount,
                    sections[i].TotalOmitted,
                    report.MaxIssuesPerCategory,
                    truncatedByBudget);
            }

            if (!string.IsNullOrEmpty(report.TruncationNote))
            {
                sb.AppendLine();
                sb.AppendLine(report.TruncationNote);
            }

            if (anyOmittedByFormat || report.IsOutputBudgetReached)
            {
                sb.AppendLine();
                sb.Append(OutputTruncationMarker);
            }

            return LimitOutput(sb.ToString().TrimEnd());
        }

        private static void AllocateSectionBudgets(AuditSection[] sections, int activeCount, int sectionsBudget)
        {
            if (activeCount <= 0) return;

            var activeIndices = new int[activeCount];
            int idx = 0;
            for (int i = 0; i < sections.Length; i++)
            {
                if (sections[i].FullLength > 0)
                {
                    activeIndices[idx++] = i;
                }
            }

            Array.Sort(activeIndices, (a, b) => sections[a].FullLength.CompareTo(sections[b].FullLength));

            int remBudget = sectionsBudget;
            int remCount = activeCount;
            for (int i = 0; i < activeCount; i++)
            {
                int secIdx = activeIndices[i];
                int fairShare = remBudget / remCount;
                int allocated = Math.Min(sections[secIdx].FullLength, fairShare);
                sections[secIdx].Budget = allocated;
                remBudget -= allocated;
                remCount--;
            }
        }

        private static string FormatCategorySummary(AuditSection[] sections)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < sections.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(sections[i].Title);
                sb.Append(": ");
                if (sections[i].TotalOmitted > 0)
                {
                    sb.Append(FormattableString.Invariant($"{sections[i].ShownCount}+ ({sections[i].TotalOmitted} omitted)"));
                }
                else
                {
                    sb.Append(sections[i].ShownCount.ToString(CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        private static string GetSectionTruncationMarker(int omittedCount, int maxIssuesPerCategory, bool outputBudgetReached)
        {
            return outputBudgetReached
                ? FormattableString.Invariant($"  ... ({omittedCount} more omitted; output budget reached)")
                : FormattableString.Invariant($"  ... ({omittedCount} more omitted; maxIssuesPerCategory={maxIssuesPerCategory})");
        }

        private static void AppendAuditSection(
            StringBuilder sb,
            string title,
            List<string> items,
            int shownCount,
            int omittedCount,
            int maxIssuesPerCategory,
            bool outputBudgetReached)
        {
            if ((items == null || items.Count == 0) && omittedCount == 0) return;

            sb.AppendLine();
            string countText = omittedCount > 0
                ? FormattableString.Invariant($"{shownCount}+, {omittedCount} omitted")
                : shownCount.ToString(CultureInfo.InvariantCulture);
            sb.AppendLine($"{title} ({countText}):");
            for (int i = 0; i < shownCount; i++)
            {
                sb.AppendLine(items[i]);
            }

            if (omittedCount > 0)
            {
                sb.AppendLine(GetSectionTruncationMarker(omittedCount, maxIssuesPerCategory, outputBudgetReached));
            }
        }

        private sealed class AuditReport
        {
            private const int PerCategoryReservedCharacters = 4096;

            public string ScopeDescription { get; set; }
            public int MaxIssuesPerCategory { get; }
            public int GameObjectsScanned { get; set; }
            public int EstimatedCharacterCount { get; private set; }
            public string TruncationNote { get; set; }

            public List<string> MissingScripts { get; } = new List<string>();
            public List<string> BrokenReferences { get; } = new List<string>();
            public List<string> BrokenUnityEvents { get; } = new List<string>();
            public List<string> MaterialAndShaderIssues { get; } = new List<string>();
            public List<string> SceneHygieneIssues { get; } = new List<string>();

            private int _missingScriptsChars;
            private int _brokenReferencesChars;
            private int _brokenUnityEventsChars;
            private int _materialAndShaderIssuesChars;
            private int _sceneHygieneIssuesChars;

            public int MissingScriptsSuppressed { get; private set; }
            public int BrokenReferencesSuppressed { get; private set; }
            public int BrokenUnityEventsSuppressed { get; private set; }
            public int MaterialAndShaderIssuesSuppressed { get; private set; }
            public int SceneHygieneIssuesSuppressed { get; private set; }

            public int TotalIssues =>
                MissingScripts.Count +
                BrokenReferences.Count +
                BrokenUnityEvents.Count +
                MaterialAndShaderIssues.Count +
                SceneHygieneIssues.Count;

            public int TotalSuppressed =>
                MissingScriptsSuppressed +
                BrokenReferencesSuppressed +
                BrokenUnityEventsSuppressed +
                MaterialAndShaderIssuesSuppressed +
                SceneHygieneIssuesSuppressed;

            public bool IsOutputBudgetReached => EstimatedCharacterCount >= DefaultMaxOutputCharacters;

            public bool AreAllCategoriesCapped =>
                MissingScripts.Count >= MaxIssuesPerCategory &&
                BrokenReferences.Count >= MaxIssuesPerCategory &&
                BrokenUnityEvents.Count >= MaxIssuesPerCategory &&
                MaterialAndShaderIssues.Count >= MaxIssuesPerCategory &&
                SceneHygieneIssues.Count >= MaxIssuesPerCategory;

            public bool IsReportFull => IsOutputBudgetReached || AreAllCategoriesCapped;

            public AuditReport(string scopeDescription, int maxIssuesPerCategory)
            {
                ScopeDescription = scopeDescription;
                MaxIssuesPerCategory = Math.Max(1, maxIssuesPerCategory);
            }

            public bool TryAddMissingScript(string message)
            {
                int suppressed = MissingScriptsSuppressed;
                bool added = TryAddToCategory(MissingScripts, ref _missingScriptsChars, ref suppressed, message);
                MissingScriptsSuppressed = suppressed;
                return added;
            }

            public bool TryAddBrokenReference(string message)
            {
                int suppressed = BrokenReferencesSuppressed;
                bool added = TryAddToCategory(BrokenReferences, ref _brokenReferencesChars, ref suppressed, message);
                BrokenReferencesSuppressed = suppressed;
                return added;
            }

            public bool TryAddBrokenUnityEvent(string message)
            {
                int suppressed = BrokenUnityEventsSuppressed;
                bool added = TryAddToCategory(BrokenUnityEvents, ref _brokenUnityEventsChars, ref suppressed, message);
                BrokenUnityEventsSuppressed = suppressed;
                return added;
            }

            public bool TryAddMaterialAndShaderIssue(string message)
            {
                int suppressed = MaterialAndShaderIssuesSuppressed;
                bool added = TryAddToCategory(MaterialAndShaderIssues, ref _materialAndShaderIssuesChars, ref suppressed, message);
                MaterialAndShaderIssuesSuppressed = suppressed;
                return added;
            }

            public bool TryAddSceneHygieneIssue(string message)
            {
                int suppressed = SceneHygieneIssuesSuppressed;
                bool added = TryAddToCategory(SceneHygieneIssues, ref _sceneHygieneIssuesChars, ref suppressed, message);
                SceneHygieneIssuesSuppressed = suppressed;
                return added;
            }

            private bool TryAddToCategory(List<string> list, ref int categoryChars, ref int suppressedCount, string message)
            {
                if (list.Count >= MaxIssuesPerCategory)
                {
                    suppressedCount++;
                    return false;
                }

                if (IsOutputBudgetReached && list.Count > 0 && categoryChars >= PerCategoryReservedCharacters)
                {
                    suppressedCount++;
                    return false;
                }

                if (list.Count > 0 && categoryChars >= DefaultMaxOutputCharacters)
                {
                    suppressedCount++;
                    return false;
                }

                int itemCost = (message != null ? message.Length : 0) + 2;
                list.Add(message);
                categoryChars += itemCost;
                EstimatedCharacterCount += itemCost;
                return true;
            }
        }
    }
}
