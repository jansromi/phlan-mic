using System.Text;

namespace PhlanMic.WindowsHost;

internal enum VbCableEndpointMatchStatus
{
    Matched,
    NoEndpointsFound,
    NoUsablePairFound,
    MultipleUsablePairsFound,
    PreferredRenderEndpointNotFound,
    PreferredRenderEndpointNotUsable,
    PreferredRenderEndpointMissingCapturePair,
    PreferredRenderEndpointAmbiguous
}

internal sealed record VbCableEndpointPair(
    AudioEndpointInfo RenderEndpoint,
    AudioEndpointInfo CaptureEndpoint,
    string MatchReason);

internal sealed record VbCableEndpointMatchResult(
    VbCableEndpointMatchStatus Status,
    VbCableEndpointPair? SelectedPair,
    IReadOnlyList<VbCableEndpointPair> CandidatePairs,
    IReadOnlyList<AudioEndpointInfo> RenderCandidates,
    IReadOnlyList<AudioEndpointInfo> CaptureCandidates,
    string? PreferredRenderEndpointId);

internal static class VbCableEndpointMatcher
{
    public static VbCableEndpointMatchResult Match(
        IReadOnlyList<AudioEndpointInfo> endpoints,
        string? preferredRenderEndpointId = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var renderCandidates = endpoints
            .Where(IsVbCableRenderCandidate)
            .OrderBy(endpoint => endpoint.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var captureCandidates = endpoints
            .Where(IsVbCableCaptureCandidate)
            .OrderBy(endpoint => endpoint.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var usablePairs = BuildUsablePairs(renderCandidates, captureCandidates);

        if (!string.IsNullOrWhiteSpace(preferredRenderEndpointId))
        {
            var preferredRenderEndpoint = renderCandidates.SingleOrDefault(
                endpoint => string.Equals(endpoint.Id, preferredRenderEndpointId, StringComparison.Ordinal));

            if (preferredRenderEndpoint is null)
            {
                return new VbCableEndpointMatchResult(
                    VbCableEndpointMatchStatus.PreferredRenderEndpointNotFound,
                    null,
                    usablePairs,
                    renderCandidates,
                    captureCandidates,
                    preferredRenderEndpointId);
            }

            if (!preferredRenderEndpoint.IsActive)
            {
                return new VbCableEndpointMatchResult(
                    VbCableEndpointMatchStatus.PreferredRenderEndpointNotUsable,
                    null,
                    usablePairs,
                    renderCandidates,
                    captureCandidates,
                    preferredRenderEndpointId);
            }

            var preferredMatches = usablePairs
                .Where(pair => string.Equals(pair.RenderEndpoint.Id, preferredRenderEndpointId, StringComparison.Ordinal))
                .ToArray();

            return preferredMatches.Length switch
            {
                1 => new VbCableEndpointMatchResult(
                    VbCableEndpointMatchStatus.Matched,
                    preferredMatches[0],
                    usablePairs,
                    renderCandidates,
                    captureCandidates,
                    preferredRenderEndpointId),
                0 => new VbCableEndpointMatchResult(
                    VbCableEndpointMatchStatus.PreferredRenderEndpointMissingCapturePair,
                    null,
                    usablePairs,
                    renderCandidates,
                    captureCandidates,
                    preferredRenderEndpointId),
                _ => new VbCableEndpointMatchResult(
                    VbCableEndpointMatchStatus.PreferredRenderEndpointAmbiguous,
                    null,
                    usablePairs,
                    renderCandidates,
                    captureCandidates,
                    preferredRenderEndpointId)
            };
        }

        if (usablePairs.Length == 1)
        {
            return new VbCableEndpointMatchResult(
                VbCableEndpointMatchStatus.Matched,
                usablePairs[0],
                usablePairs,
                renderCandidates,
                captureCandidates,
                null);
        }

        if (usablePairs.Length > 1)
        {
            return new VbCableEndpointMatchResult(
                VbCableEndpointMatchStatus.MultipleUsablePairsFound,
                null,
                usablePairs,
                renderCandidates,
                captureCandidates,
                null);
        }

        return new VbCableEndpointMatchResult(
            renderCandidates.Length == 0 && captureCandidates.Length == 0
                ? VbCableEndpointMatchStatus.NoEndpointsFound
                : VbCableEndpointMatchStatus.NoUsablePairFound,
            null,
            usablePairs,
            renderCandidates,
            captureCandidates,
            null);
    }

    private static VbCableEndpointPair[] BuildUsablePairs(
        IReadOnlyList<AudioEndpointInfo> renderCandidates,
        IReadOnlyList<AudioEndpointInfo> captureCandidates)
    {
        var usableRenders = renderCandidates.Where(endpoint => endpoint.IsActive).ToArray();
        var usableCaptures = captureCandidates.Where(endpoint => endpoint.IsActive).ToArray();

        if (usableRenders.Length == 0 || usableCaptures.Length == 0)
        {
            return Array.Empty<VbCableEndpointPair>();
        }

        var pairs = (
                from render in usableRenders
                let renderFamilyKey = ExtractFamilyKey(render.FriendlyName)
                from capture in usableCaptures
                let captureFamilyKey = ExtractFamilyKey(capture.FriendlyName)
                where string.Equals(renderFamilyKey, captureFamilyKey, StringComparison.Ordinal)
                select new VbCableEndpointPair(render, capture, "family-key"))
            .ToArray();

        if (pairs.Length > 0)
        {
            return pairs;
        }

        return usableRenders.Length == 1 && usableCaptures.Length == 1
            ? new[] { new VbCableEndpointPair(usableRenders[0], usableCaptures[0], "single-active-render-capture") }
            : Array.Empty<VbCableEndpointPair>();
    }

    private static bool IsVbCableRenderCandidate(AudioEndpointInfo endpoint)
    {
        if (endpoint.Flow is not AudioEndpointFlow.Render)
        {
            return false;
        }

        var normalizedName = Normalize(endpoint.FriendlyName);
        return IsVbCableLike(normalizedName) &&
               (normalizedName.Contains("cableinput", StringComparison.Ordinal) ||
                !normalizedName.Contains("cableoutput", StringComparison.Ordinal));
    }

    private static bool IsVbCableCaptureCandidate(AudioEndpointInfo endpoint)
    {
        if (endpoint.Flow is not AudioEndpointFlow.Capture)
        {
            return false;
        }

        var normalizedName = Normalize(endpoint.FriendlyName);
        return IsVbCableLike(normalizedName) &&
               (normalizedName.Contains("cableoutput", StringComparison.Ordinal) ||
                !normalizedName.Contains("cableinput", StringComparison.Ordinal));
    }

    private static bool IsVbCableLike(string normalizedName) =>
        normalizedName.Contains("vbaudiovirtualcable", StringComparison.Ordinal) ||
        normalizedName.Contains("vbcable", StringComparison.Ordinal) ||
        normalizedName.StartsWith("cable", StringComparison.Ordinal);

    private static string ExtractFamilyKey(string friendlyName)
    {
        var normalizedName = Normalize(friendlyName);
        var cableToken = ExtractCableToken(normalizedName);
        var parentheticalToken = ExtractParentheticalToken(friendlyName);
        return $"{cableToken}|{parentheticalToken}";
    }

    private static string ExtractCableToken(string normalizedName)
    {
        var cableIndex = normalizedName.IndexOf("cable", StringComparison.Ordinal);
        if (cableIndex < 0)
        {
            return "cable";
        }

        var builder = new StringBuilder();

        for (var index = cableIndex; index < normalizedName.Length; index++)
        {
            var current = normalizedName[index];
            if (!char.IsLetterOrDigit(current))
            {
                break;
            }

            builder.Append(current);
        }

        var token = builder.ToString();
        return token
            .Replace("input", string.Empty, StringComparison.Ordinal)
            .Replace("output", string.Empty, StringComparison.Ordinal);
    }

    private static string ExtractParentheticalToken(string friendlyName)
    {
        var openIndex = friendlyName.LastIndexOf('(');
        var closeIndex = friendlyName.LastIndexOf(')');
        if (openIndex < 0 || closeIndex <= openIndex)
        {
            return Normalize(friendlyName.Replace("Input", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("Output", string.Empty, StringComparison.OrdinalIgnoreCase));
        }

        return Normalize(friendlyName[(openIndex + 1)..closeIndex]);
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var current in value)
        {
            if (char.IsLetterOrDigit(current))
            {
                builder.Append(char.ToLowerInvariant(current));
            }
        }

        return builder.ToString();
    }
}
