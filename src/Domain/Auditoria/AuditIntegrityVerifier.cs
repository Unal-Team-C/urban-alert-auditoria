using System.Text.Json;

namespace Domain.Auditoria;

public sealed record AuditChainEntry(long Sequence, Guid? ReportId, JsonElement Payload, string PreviousHash, string Hash);

public sealed record AuditIntegrityResult(bool Verified, int ReportEventCount, int ChainEventCount, bool HeadMatches, long? BrokenSequence);

public static class AuditIntegrityVerifier
{
    public static AuditIntegrityResult Verify(IEnumerable<AuditChainEntry> entries, string? storedHead, Guid reportId)
    {
        List<AuditChainEntry> chain = entries as List<AuditChainEntry> ?? entries.ToList();
        string expectedPrevious = AuditHash.GenesisHash;
        long? brokenSequence = null;
        int reportEventCount = 0;
        int chainEventCount = chain.Count;

        foreach (AuditChainEntry entry in chain)
        {
            string calculatedHash = AuditHash.Compute(entry.Payload, expectedPrevious);
            if (!string.Equals(entry.PreviousHash.Trim(), expectedPrevious, StringComparison.Ordinal) ||
                !string.Equals(entry.Hash.Trim(), calculatedHash, StringComparison.Ordinal))
            {
                brokenSequence = entry.Sequence;
                break;
            }

            expectedPrevious = entry.Hash.Trim();
            if (entry.ReportId == reportId)
                reportEventCount++;
        }

        bool headMatches = string.Equals(storedHead?.Trim(), expectedPrevious, StringComparison.Ordinal);
        return new AuditIntegrityResult(brokenSequence is null && headMatches, reportEventCount,
            chainEventCount, headMatches, brokenSequence);
    }
}
