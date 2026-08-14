using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed partial class CodexSessionReader
{
    /// <summary>
    /// Resolves physical rollout files into one local token ledger. The strict
    /// parent/child longest-common-prefix rule follows the MIT-licensed codexU
    /// v1.1.5 approach; explicit modern thread_spawn boundaries take precedence.
    /// See THIRD-PARTY-NOTICES.md.
    /// </summary>
    private static SessionReconstruction ReconstructSessions(
        IReadOnlyList<PhysicalSessionFile> physicalFiles)
    {
        var canonical = new List<PhysicalSessionFile>(physicalFiles.Count);
        var duplicateFiles = 0;
        var divergentDuplicates = 0;

        foreach (var group in physicalFiles
                     .Where(file => !string.IsNullOrWhiteSpace(file.Parsed.SessionId))
                     .GroupBy(file => file.Parsed.SessionId!, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = group.ToArray();
            var (selected, divergent) = SelectCanonical(candidates);
            canonical.Add(selected);
            duplicateFiles += candidates.Length - 1;
            if (divergent)
            {
                divergentDuplicates++;
            }
        }

        canonical.AddRange(physicalFiles.Where(file => string.IsNullOrWhiteSpace(file.Parsed.SessionId)));

        var bySessionId = canonical
            .Where(file => !string.IsNullOrWhiteSpace(file.Parsed.SessionId))
            .ToDictionary(file => file.Parsed.SessionId!, StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ResolvedSessionFile>(canonical.Count);
        var structuralForks = 0;
        var prefixForks = 0;
        var ambiguousForks = 0;
        long ambiguousForkTokens = 0;

        foreach (var file in canonical)
        {
            var parsed = file.Parsed;
            var prefixLength = 0;
            if (parsed.ForkReplayPhase == ForkReplayPhase.Completed)
            {
                structuralForks++;
            }
            else
            {
                var lineageParentId = parsed.ForkedFromId
                    ?? (parsed.ForkReplayPhase is ForkReplayPhase.AwaitingParentMetadata or ForkReplayPhase.Replaying
                        ? parsed.ForkReplayParentId
                        : null);
                if (string.IsNullOrWhiteSpace(lineageParentId))
                {
                    resolved.Add(new ResolvedSessionFile(file, prefixLength));
                    continue;
                }

                if (bySessionId.TryGetValue(lineageParentId, out var parent)
                    && !ReferenceEquals(parent, file))
                {
                    prefixLength = LongestCommonPrefix(
                        parsed.TokenEvents,
                        parent.Parsed.TokenEvents);
                    if (prefixLength > 0)
                    {
                        prefixForks++;
                    }
                    else
                    {
                        MarkAmbiguousFork(parsed, ref ambiguousForks, ref ambiguousForkTokens);
                    }
                }
                else
                {
                    MarkAmbiguousFork(parsed, ref ambiguousForks, ref ambiguousForkTokens);
                }
            }

            resolved.Add(new ResolvedSessionFile(file, prefixLength));
        }

        return new SessionReconstruction(
            resolved,
            duplicateFiles,
            divergentDuplicates,
            structuralForks,
            prefixForks,
            ambiguousForks,
            ambiguousForkTokens);
    }

    private static void MarkAmbiguousFork(
        ParsedSessionFile parsed,
        ref int ambiguousForks,
        ref long ambiguousForkTokens)
    {
        var retained = parsed.TokenEvents
            .Sum(tokenEvent => tokenEvent.Tokens.VisibleTotalTokens);
        if (retained <= 0)
        {
            return;
        }

        ambiguousForks++;
        ambiguousForkTokens += retained;
    }

    private static (PhysicalSessionFile Selected, bool Divergent) SelectCanonical(
        IReadOnlyList<PhysicalSessionFile> candidates)
    {
        var selected = candidates[0];
        var divergent = false;
        for (var index = 1; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var common = LongestCommonPrefix(
                selected.Parsed.TokenEvents,
                candidate.Parsed.TokenEvents);
            var selectedIsPrefix = common == selected.Parsed.TokenEvents.Count;
            var candidateIsPrefix = common == candidate.Parsed.TokenEvents.Count;

            if (!selectedIsPrefix && !candidateIsPrefix)
            {
                divergent = true;
            }

            if ((selectedIsPrefix && !candidateIsPrefix)
                || (selectedIsPrefix && candidateIsPrefix && IsMoreComplete(candidate, selected))
                || (!selectedIsPrefix && !candidateIsPrefix && IsMoreComplete(candidate, selected)))
            {
                selected = candidate;
            }
        }

        return (selected, divergent);
    }

    private static bool IsMoreComplete(PhysicalSessionFile candidate, PhysicalSessionFile current)
    {
        var eventComparison = candidate.Parsed.TokenEvents.Count.CompareTo(current.Parsed.TokenEvents.Count);
        if (eventComparison != 0)
        {
            return eventComparison > 0;
        }

        var offsetComparison = candidate.Parsed.Offset.CompareTo(current.Parsed.Offset);
        if (offsetComparison != 0)
        {
            return offsetComparison > 0;
        }

        var writeComparison = candidate.LastWriteTimeUtcTicks.CompareTo(current.LastWriteTimeUtcTicks);
        if (writeComparison != 0)
        {
            return writeComparison > 0;
        }

        return string.Compare(candidate.Path, current.Path, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static int LongestCommonPrefix(
        IReadOnlyList<SessionTokenEvent> first,
        IReadOnlyList<SessionTokenEvent> second)
    {
        var maximum = Math.Min(first.Count, second.Count);
        var index = 0;
        while (index < maximum && first[index].Identity == second[index].Identity)
        {
            index++;
        }

        return index;
    }

    private static IEnumerable<SessionTokenEvent> EffectiveTokenEvents(ResolvedSessionFile session)
    {
        var parsed = session.Source.Parsed;
        var events = parsed.TokenEvents;
        var hasCompleteStructuralBoundary = parsed.ForkReplayPhase == ForkReplayPhase.Completed;
        for (var index = 0; index < events.Count; index++)
        {
            var tokenEvent = events[index];
            if (index < session.ForkPrefixLength
                || (hasCompleteStructuralBoundary && tokenEvent.IsStructuralReplay))
            {
                continue;
            }

            yield return tokenEvent;
        }
    }

    private sealed record PhysicalSessionFile(
        string Path,
        long LastWriteTimeUtcTicks,
        ParsedSessionFile Parsed);

    private sealed record ResolvedSessionFile(
        PhysicalSessionFile Source,
        int ForkPrefixLength);

    private sealed record SessionReconstruction(
        IReadOnlyList<ResolvedSessionFile> Files,
        int DuplicateFileCount,
        int DivergentDuplicateCount,
        int StructuralForkCount,
        int PrefixForkCount,
        int AmbiguousForkCount,
        long AmbiguousForkTokens);
}
