using System.Text.RegularExpressions;
using FellowOakDicom;

namespace MedMissionBridge.Dicom;

public static class WorklistMatcher
{
    public static bool Matches(DicomDataset query, DicomDataset item)
    {
        var qId = Get(query, DicomTag.PatientID);
        if (qId.Length > 0 && qId != Get(item, DicomTag.PatientID)) return false;

        var qName = Get(query, DicomTag.PatientName);
        if (qName.Length > 0 && !WildcardMatches(qName, Get(item, DicomTag.PatientName))) return false;

        if (query.TryGetSequence(DicomTag.ScheduledProcedureStepSequence, out var qSeq)
            && qSeq.Items.Count > 0)
        {
            var q = qSeq.Items[0];
            var i = item.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0];

            var qModality = Get(q, DicomTag.Modality);
            if (qModality.Length > 0 && qModality != Get(i, DicomTag.Modality)) return false;

            var qDate = Get(q, DicomTag.ScheduledProcedureStepStartDate);
            if (qDate.Length > 0
                && !DateMatches(qDate, Get(i, DicomTag.ScheduledProcedureStepStartDate)))
                return false;
        }
        return true;
    }

    private static string Get(DicomDataset ds, DicomTag tag) =>
        ds.GetSingleValueOrDefault(tag, string.Empty);

    private static bool WildcardMatches(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private static bool DateMatches(string queryDate, string itemDate)
    {
        if (itemDate.Length == 0) return false;
        var dash = queryDate.IndexOf('-');
        if (dash < 0) return itemDate == queryDate;
        // A malformed multi-dash range (more than one '-') is trusted-SCU input
        // (typed by the modality operator, not attacker-controlled over the
        // network); splitting on the first dash just yields a nonsense
        // from/to pair that safely matches nothing rather than throwing.
        var from = queryDate[..dash];
        var to = queryDate[(dash + 1)..];
        // yyyyMMdd strings compare correctly as ordinals
        if (from.Length > 0 && string.CompareOrdinal(itemDate, from) < 0) return false;
        if (to.Length > 0 && string.CompareOrdinal(itemDate, to) > 0) return false;
        return true;
    }
}
